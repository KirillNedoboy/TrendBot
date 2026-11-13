# Current checkpoint

## Public market context (2026-11-13)

- User-authorized explanatory context adds UTC-session VWAP, a recent
  observed-trade POC and sampled Binance liquidation snapshots to the 12-symbol,
  four-timeframe public scanner. None changes Lane A/B/C admission. VWAP uses
  exchange quote/base volumes from contiguous closed bars; POC uses the covered
  one-minute Lane C window and configured per-symbol price increment. Missing
  inputs remain unknown. The POC is not a full-session volume profile.
- The separate public liquidation stream is Binance's incomplete snapshot, not
  total liquidations or a heatmap. No observed snapshot is not a zero count.
  Its outage clears this optional context without resetting the B/C feeds.
  Event, exchange-trade and availability timestamps are checked at the decision
  cut. Evidence v3 commits the context with a new signal; v1/v2 remain readable.
- An isolated real run exposed a bar closing between initial REST warmup and
  WebSocket subscription. The first open update now backfills this gap; a close
  received before recovery remains unknown and history never creates a
  retrospective signal. The isolated BTC 5m ledger subsequently recovered the
  missing 18:30 UTC bar from public REST. The old T019 journal remained readable.
- At the next actual 5m close in the isolated run, all 12 symbols had known
  VWAP and POC; observed liquidation snapshots were present for BTC, ETH and
  XRP. These observations do not establish future feed availability or trading
  effectiveness.
- Persistent session process `signal_12_context_v5` uses the existing
  `artifacts/signal-only-12/` state and multi-timeframe configuration; all 48
  symbol/profile streams reported receiving. The BTC 15m journal retained
  checkpoint 9, zero signals and nine diagnostics through the cutover. The
  process is not installed as a supervised service.
- Targeted checks passed: Core 26/26, Binance 11/11 and integration 70/70;
  Release builds had zero warnings/errors and scoped whitespace verification
  passed. Telegram formatting used a fake HTTP handler; no external send, order,
  private execution or OOS comparison was performed.

## Four-timeframe public research scanner (2026-11-13)

- The user authorized 5m/15m, 15m/1h, 30m/1h and 1h/4h setup/context
  profiles on all 12 configured USD-M symbols. The v4 example retains the
  original 15m/1h identity and settings and adds separately versioned profiles,
  cursors, scenarios and journal streams. Kline subscriptions are deduplicated;
  trade, depth, mark and OI sources are shared per symbol. All three lanes
  participate, with missing/stale or vetoed B/C inputs blocking a candidate.
- New profile values are explicit exploratory assumptions: indicator periods
  remain bar-relative, freshness is two bars, scenario expiry is 96 setup bars,
  and the existing one-minute B/C observations are reused at each decision.
  This does not establish equivalence between timeframes, statistical validity,
  or profitability. The fixed history start implies growing warmup/storage; no
  24/7 soak, resource quota or installed supervisor is claimed.
- On isolated real state, all 12 symbols reached all four public streams and
  observed trade, mark, recovered depth, a complete trade window and two OI
  observations. The next actual 5m close yielded 12 ready `no-confirmed-setup`
  diagnostics and no fabricated signal. An actual restart recovered the four
  stream histories and resumed their public subscriptions. The other new
  timeframes had not yet reached a live decision close in that isolated run.
- The previous v3 process was stopped after the isolated run. The subsequent
  `signal_12_multitimeframe_v4` session used the existing
  `artifacts/signal-only-12/` state and `signal-only.multitimeframe.example.json`;
  all 48 symbol/profile streams reported receiving. That process was stopped
  for the context cutover above. The BTC 15m journal retained
  its prior checkpoint 5 and five diagnostics across the cutover. At the next
  common close all 48 setup inputs were accepted with ready price evaluation:
  46 `no-confirmed-setup` diagnostics and two `multi-lane-blocked` candidates
  (XRP 30m and SUI 5m; B inputs and C coverage were not yet ready), no signals.
  This process is not installed or supervised and Telegram was not enabled.
  After that initial close, the running process observed a complete trade window
  and an OI pair for all 12 symbols; arrival does not guarantee future freshness.
- Targeted checks passed: Core 37/37, Binance 18/18 and integration 81/81.
  Fixtures cover signal identity/provenance, all new timeframe boundaries,
  confirmed/veto/unknown lane decisions, duplicate and restart handling, both
  co-close arrival orders, shared subscription routing, and grouped Telegram
  acknowledgment, ambiguity and rate limiting. Grouped delivery uses only fake
  HTTP in tests. Scoped C# formatting and the docs validator were run; no
  external Telegram send, OOS comparison, order or private execution was made.

## Twelve-symbol public research scanner (2026-11-13)

- The user requested 12 symbols and participation by all three lanes. Format-v3
  `signal-only.12.example.json` configures BTC, ETH, SOL, BNB, XRP, LINK, AVAX,
  ADA, SUI, HYPE, DOGE and 1000PEPE USDT perpetuals with isolated state.
  All 12 were publicly observed as Binance TRADING/PERPETUAL. The per-symbol
  minimum channel widths are the observed one-tick increments, not validated
  strategy parameters or automatically refreshed exchange filters.
- Lane A supplies a candidate; Lane B compares two causal OI/mark-price pairs;
  Lane C compares the recovered top-10 book and observed aggregate-trade flow.
  Both B/C must be Confirm or Neutral. Veto, missing, stale or incomplete
  evidence blocks the candidate; future observations are ineligible. Funding is
  contextual, not directional proof.
  The policy, event and availability times, and evidence for admitted signals
  are persisted in the same journal transaction. The explicit rules and source
  limitations are in the [runbook](docs/architecture/operations.md#p15-m03-t011-public-only-signal-runtime);
  this is an exploratory hypothesis, not validated profitability or OOS evidence.
- Public smoke runs observed price, mark, trade, recovered depth and OI for all
  12 symbols, including two OI observations per symbol. A previous run stopped
  on `BinanceApiException` without recording its status or endpoint. Temporary
  public REST failures now invalidate the affected input and retry; non-transient
  API failures stop with a sanitized status and endpoint. Subscription and
  heartbeat acknowledgments no longer falsely invalidate the depth book.
  A real USD-M stream showed consecutive events whose `pu` matched the prior
  `u` while `U` was greater than `u + 1`; recovery had incorrectly rejected
  these events. Recovery now follows the venue's `pu` linkage, retaining the
  numeric-range fallback only when `pu` is absent. A mismatched `pu` still
  invalidates the book. Recovered checkpoints use the post-diff ID and causal
  times; old captures were not rewritten and future events remain ineligible.
- After the fix, all 12 live streams reported a complete trade-window observation
  with two causally visible recovered books and two OI observations each. These
  are source-arrival observations, not proof of continued health or of a
  prospective multi-lane signal. An earlier real 15m close recorded 11
  `no-confirmed-setup`, one `signal-evaluation-unknown`, zero signals and zero
  outbox rows across the 12 symbols; it preceded the book fix and was not rewritten.
- The live close diagnostics also report missing VWAP/POC window configuration
  and a missing price-volume scenario TTL. Those price-volume inputs remain
  Unknown; the separate boundary and center setup families can still be assessed.
- Targeted tests: Core 30/30, Integration 77/77 and Binance 18/18; Release
  test builds had zero warnings/errors and scoped whitespace verification passed.
  Restart, unavailable-input, linked book updates and bounded trade-buffer
  behavior have deterministic fixture coverage; no 24/7 soak has been performed.
- The corrected format-v3 scanner ran as a persistent session process using
  `artifacts/signal-only-12/`; it was stopped for the v4 cutover above. Its
  journal and the obsolete BTC-only process's state files were preserved.
  Neither process was an installed or automatically restarted service.
  No Telegram send, private execution, order, paper/testnet operation or OOS
  comparison was performed. No commit or push.

## Public signal runtime integration (2026-11-13)

- The user requested an operating bot rather than treating optional OOS research as a startup prerequisite. The public-only BTCUSDT signal path is independent of T022; research validation remains `UNKNOWN`. No task status or OOS criteria were changed.
- `signal-only --config` now supports explicit optional `--telegram-state <file>` in the same process. The existing committed-outbox sender and durable delivery state are reused; ambiguous delivery remains terminal, existing outbox rows cannot be paired with a missing state file, and no Telegram credentials are stored in configuration or logs. Signal and scenario times are included in messages.
- The live consumer ignores open-candle updates, waits for the nominal close, reports actual public stream/close/signal events, and backfills incomplete native histories after restart or a recoverable stream interruption. Backfilled receipt hashes are refreshed before a later signal can use them as provenance; history backfill does not create retrospective signals.
- Actual public CLI start and restart with `signal-only.public.example.json` both reached `public-stream-receiving` after loading 517 real native input receipts. The restarted process then accepted an actual closed 15m input with readiness `ready`; the local journal recorded one `no-confirmed-setup` diagnostic, zero signals and zero pending outputs. No signal or publication was invented. The persistent local process uses `artifacts/signal-only-public/`; it is not an installed or automatically restarted service.
- Targeted Integration tests passed 44/44, including recovery with a backfilled signal-provenance bar, public stream interruption, committed local output, continuous delivery, cancellation and ambiguous-delivery idempotency. The final Release App build passed with zero warnings/errors and scoped C# whitespace verification passed. A separate CLI check using a copy of the actual T019 journal refused a missing delivery-state file before creating one or sending. Documentation validation passed: 19 phases, 43 milestones, 152 tasks, zero READY with explicit blockers.
- Telegram environment settings were absent; no Telegram message, order, private execution, paper/testnet operation or OOS comparison was performed. The process has bounded WebSocket reconnect attempts and no 24/7 soak, supervisor, storage quota or profitability claim. Preserve the state files and follow the [public signal bot runbook](docs/architecture/operations.md#public-signal-bot). No commit or push.

## P15-M03-T021 (SR-08.2) — DONE (2026-11-13)

- Began only after T020 acceptance. Added the operator runbook in [operations baseline](docs/architecture/operations.md#p15-m03-t021-public-only-start-recovery-and-verdict): pinned SDK/locked restore and Release build, preflight/hash checks, public-only bounded start and planned restart, exact-identity resume, read-only local rendering and fail-closed recovery guidance. `preflight ready` is not runtime readiness.
- The runbook separates the baseline software verdict (`PASS` for the checked software path), the research state (`RESEARCH` / statistical validation `UNKNOWN`) and execution authorization (not granted). T019 had a real signal, local output row and decision-clock outcome, but no external publication. T014's Telegram acknowledgment is for its separate TESTONLY message only.
- Isolated start/reopen smoke copied the actual T019 500-receipt native ledger, initialized a fresh journal through feed/consumer recovery, reopened both stores and verified exact journal parity with T019: checkpoint 12, one signal, one outbox row, 11 diagnostics. The HTTP-rejecting handler observed zero requests. The actual App renderer process reported one pending local output, zero receipts and null `publishedAt`, with `RESEARCH` / `UNKNOWN`. T019 separately retains the actual bounded process-restart evidence; this smoke was offline and did not create a new live capture.
- Docs validator fixtures passed 11/11; native docs validator and canonical source hash passed after the runbook update. No OOS comparison, external send, private execution, orders or paper/testnet. LimitedLive user changes were left untouched; no commit or push. No Linux or remote-CI result is claimed. T022 remains blocked on separate research authorization and suitable frozen data.

## P15-M03-T020 (SR-08.1) — DONE (2026-11-13)

- The startup selector chose T020: first READY after T019=DONE; T020's only dependency was T019. Accepted the requested baseline block only; SR-07/OOS comparison, orders, private execution, paper/testnet and external sending were not run.
- Independently checked the ignored local T019 evidence. Its public bounded run contains 12/12 real closed 15m inputs, 3/3 real closed 1h inputs, one genuine committed signal, one local output-only outbox row and one persisted decision-clock outcome; publication is `not-observed`. T014's Telegram acknowledgment belongs to its separate TESTONLY message and is not T019 delivery evidence.
- Offline E2E smoke copied the real 500-receipt SQLite input ledger to an isolated store, restored the native feed and consumer, and reproduced the real signal-journal snapshot exactly. Replay counts: setup/context 400/100; checkpoint 12; one signal, one scenario event and one output-only outbox row; 11 diagnostics. Recomputed outcome canonical JSON matched the persisted real decision-clock outcome (`Complete`, one covered bar); a duplicate append was idempotent. The local renderer showed one pending output, zero delivery receipts, null `publishedAt`, `RESEARCH`/`UNKNOWN`; rejecting HTTP handler observed zero requests. This was offline replay of actual T019 receipts, not a second live run or fabricated evidence.
- Focused checks: Integration 55/55 (native input, causal cut, recovery, failure, redaction and zero private/order composition); Infrastructure SQLite 15/15; outcome observer 7/7. App and relevant test projects built Release with `--warnaserror`; locked restore used pinned SDK 10.0.401; scoped format passed. Docs fixtures passed 11/11 after isolating fixture READY states; validator passed. Canonical `FINAL_TECH_SPEC.md` SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- The independent replay used a current Release build of the verified T019 source set; its assembly hashes differ from T019's frozen historical binaries. Exact semantic/journal/outcome parity was verified on the isolated replay. No Linux or remote-CI run is claimed. T021 is DONE; T022 remains blocked on separate authorization and suitable frozen data.

## P15-M03-T019 (SR-06.2) — DONE (2026-11-13)

- **Separate T019 acceptance:** After T018 acceptance, the predeclared extended public-only run completed after an actual process restart. It captured 12/12 15m and 3/3 1h closes, one genuine committed signal, one output-only local outbox row, one persisted decision-clock outcome and 11 NoSetup diagnostics; no external publication was observed. The outcome is an OHLC directional observation over one real eligible bar, not a fill, net result or trading-profit claim.
- Independent offline replay verified all 12 causal prefixes and 5,913 input cuts, one local-signal output and one outcome path; live and restarted semantic SHA-256 matched `ff9e6b3b96d2a9dd99fa32408de7cad7f8e8e31c8c6533b1614a85c2e54eeaff`, with zero offline network calls. Actual restart took 0.3145046 seconds and graceful stop 0.6469287 seconds. A real disconnect in an earlier run failed closed; targeted gap, database-failure and recovery checks passed. Final targeted integration tests passed 21/21 and scoped format passed.
- The run used at most 14,400 seconds; its recorded elapsed duration was approximately 10,515 seconds. Across 2,103 health samples, peak working set was 113,836,032 bytes and peak logical store size was 8,659,415 bytes, within the declared sampled limits; sampling is not an instantaneous OS quota. Evidence: `artifacts/P15-M03-T019/t019-extended-20261113T070421Z-3d1d1d99a6bc4d429d8b519b78e813c9-causal-audit.json`.
- **Scope boundary:** T020 is now READY in the task graph but was not authorized or started in this request. T022/OOS comparison remains blocked on separate authorization and suitable frozen data; no OOS comparison, orders or private execution occurred.

- **First prospective attempt did not pass:** The bounded CLI captured three actual closed 15m decisions and one closed 1h input. All three committed diagnostics were `no-confirmed-setup` with ready evaluation; there were no signals, outbox rows or outcomes. The planned first-process stop at `21:00:00.2452959Z` and resumed-process start at `21:00:00.5980908Z` were recorded. The second process stopped with `runtime-failure` / `InvalidOperationException` before its replay journal was created; that attempt was not accepted.
- On an isolated copy of that real journal, ordinary `System.Text.Json` Web serialization reproduced `InvalidOperationException`: `SignalRecord.Probability` is required but has no setter. The existing `CoreJsonSerializer` serialized the same journal successfully. The bounded replay now uses the Core serializer; the committed-signal/reopen regression and related preflight/composition cases passed 16/16, the App Release build has zero warnings/errors, and scoped format passed. The failed run is retained under `artifacts/P15-M03-T019/t019-20261112T201038Z-3417cf1057c34e55b0b588257c5b41ea/`.

- T018 was accepted before implementation began. The frozen envelope and preflight evidence are recorded below. The new bounded public CLI validates its envelope hash, limits REST response reads, uses isolated page-bounded SQLite stores, stops on required-source failures, emits coverage/health and local journal output, and independently replays recorded inputs. Process restart uses the original deadline and bound assembly hashes. Resource sampling is not an OS-level instantaneous quota.
- Earlier targeted checks: Integration preflight, public composition and recovery/failure cases 22/22; public REST/stream 12/12; SQLite ledger/journal 15/15; outcome observer 7/7. A bounded-runtime disconnect fixture proves cancellation and clears Ready.
- An independent offline replay of the first run verified all three causal prefixes and 1,308 input-cut checks, plus restarted journal parity, with zero network calls. It retained three NoSetup, zero Unknown/signals/outbox and created no outcomes; input hash `20618e7fa34db472097e6610fe05dc7242b2feef1d5e56258989144a309aeda3`, journal hash `1883709d052ff48fd9804911e0ef51e26e4fa3350b9914cf601447e9b6cfa591`. This does not turn the failed runtime attempt into an accepted run.
- A separately predeclared retry is `artifacts/P15-M03-T018/t019-retry-20261112T213434Z-2686fea54e0d49a4b93afa358df2e951/envelope.json`, SHA-256 `d0d19d820ce7423d39e1ff0c6752d50b3346a1d78bc6f7c46e0c4d15b365b86c`. It differs from the accepted T018 envelope only in run ID and isolated file paths; the profile and policy hashes are identical. Its actual preflight returned `ready`; selected source and assembly hashes were frozen before the retry in `pre-run-evidence.json`, SHA-256 `750de1b8bb831b4588b9c2e7e8beadafa700ecffd684bdc1cd544013caf5d6aa`. No new trading thresholds or OOS comparison were introduced.
- **Retry evidence:** The separately frozen retry completed with exit 0 after an actual process restart. It observed 3/3 prospective 15m and 1/1 1h closes, three ready `no-confirmed-setup` diagnostics, zero Unknown, signals, outbox rows or outcomes. Independent offline replay checked all three prefixes and 1,319 causal input cuts; restarted and continuous semantic hashes matched `2a12b43edc7629e14ae59d380e92ccd33e0fa37a0fdce77d27959e2d579a6904`, with zero offline network calls. Actual restart was 0.2847454 seconds and final graceful stop 0.2107562 seconds. Sampled peak working set was 107,143,168 bytes and logical storage 5,198,436 bytes. No publication was observed.
- **Earlier blocker:** The first two completed runs produced no real signal and did not exercise the specified prospective signal-to-output-to-outcome chain. NoSetup is a valid decision, not evidence of a delivered signal or an observed outcome. These results remain retained; do not invent a signal or claim trading profitability. The first completed retry evidence is `artifacts/P15-M03-T019/t019-retry-20261112T213434Z-2686fea54e0d49a4b93afa358df2e951-causal-audit.json`; run files are in the matching directory.
- One additional bounded observation attempt was declared before capture at `artifacts/P15-M03-T018/t019-observe-20261112T221837Z-6432e6f112c04b96848d391f0122343a/envelope.json`, SHA-256 `3be77811194da82915431dbfc98daa1501fa372399cce9271bb98eeb92b3a304`; actual preflight returned `ready`. All fields except run ID and isolated paths match the prior retry, including the profile, policies and bounds. Its pre-run source/assembly evidence is SHA-256 `4bbe8740a56625d380ba654773e3056e0fde2db50149520760ca061cd39f7c89`. This is software-path observation, not OOS or an edge search; prior no-signal evidence remains retained.
- **Additional observation evidence:** The additional run completed with exit 0, actual restart, 3/3 prospective 15m and 1/1 1h closes, three NoSetup diagnostics, zero Unknown/signals/outbox/outcomes and no observed publication. Independent replay checked all three prefixes and 1,330 causal input cuts; restarted/live hashes matched `19c505e9f0fe2296564fea24e82aa41f1f30ef0b83371c0b2ee6c5d0f08f2aec`, with zero offline network calls. Restart took 0.2792187 seconds, final graceful stop 0.2990282 seconds; sampled peak working set was 105,730,048 bytes and logical storage 5,215,376 bytes. Evidence: `artifacts/P15-M03-T019/t019-observe-20261112T221837Z-6432e6f112c04b96848d391f0122343a-causal-audit.json`.
- Assessment of the unobserved outcome path found that native Core bar closes are already end-exclusive: adding one millisecond when creating outcome bars would make adjacent real bars overlap. The bounded runner now preserves the existing close and waits for actual closed/available input through every signal horizon before outcome creation; the run deadline without that input stops fail-closed. Focused deterministic fixture tests cover adjacent bars, horizon availability and event-time guards. These fixes were made after the two completed public runs and have not themselves had a prospective signal/outcome run.
- Verification after the outcome fixes: 18/18 targeted integration cases passed, the App Release build passed with zero warnings/errors, and scoped App/integration formatting passed. An actual bounded-run CLI invocation with an incorrect envelope hash returned `envelope-hash-mismatch` before opening stores or contacting an exchange.
- The post-fix bounded observation used `artifacts/P15-M03-T018/t019-postfix-20261112T231210Z-7a7d67decbf848f8a813654c48dfbd30/envelope.json`, SHA-256 `fe86bc2453324f752b9d4279495baeee39aca50bb77603c31535bf44fb1740f4`; selected source and assembly evidence was frozen before launch, SHA-256 `39872e356422d4701861dd0a2691f4bfaa45fb42073a2ae235ea432a57ebf584`. It restarted but failed closed with `prospective-coverage-incomplete`: at the shared hour boundary the 1h close was processed while the coincident 15m close remained absent, giving 3/4 setup and 1/1 context coverage. Its journal contains three diagnostics and no signals or outbox rows; this run is not accepted.
- The bounded runner now waits for complete coverage of both timeframes at one cutoff before planned restart or completion, instead of stopping as soon as minimum counts are met. Regression cases cover either close arriving first and reject duplicate counts; 20/20 targeted integration cases, scoped formatting and the warning-as-error App Release build passed. The fix does not invent a missing input or extend the original deadline.
- The coverage-fix observation used `artifacts/P15-M03-T018/t019-coveragefix-20261113T000422Z-8953700ad0ac4a669631569646bd5ab7/envelope.json`, SHA-256 `58864b0a379d1b98620ccf7417116074d8d3558ec8973593a995eb6f003292be`; pre-run source/assembly evidence is `2ff30b807fcd7e1c9f86314b0bffadb8f006c94592ef54636d426e2b4274a56f`. It committed one real signal, one output-only outbox and three NoSetup diagnostics, with no publication; replay journal construction succeeded, but the run then stopped with `runtime-failure` / `ArgumentException` before persisting an outcome. It is not accepted.
- Offline on isolated copies, the failure reproduced as `Invalid closed UTC outcome bar`: three actual close receipts preceded nominal bar close. The observer adapter now derives usability as the maximum of the preserved receipt availability, event time and end-exclusive close, without rewriting original timestamps. Four independent prefixes, 1,811 causal input cuts, restart parity and the real local signal output matched with zero network calls; evidence: `artifacts/P15-M03-T019/t019-coveragefix-20261113T000422Z-8953700ad0ac4a669631569646bd5ab7-failed-run-causal-audit.json`.
- On the captured real signal and causally available subsequent bars, a separate offline diagnostic on the fixed assembly obtained one complete decision-clock observation over one bar; no outcome was written, no publication was observed, and the directional metric is not realized profitability. This is not a prospective acceptance or an OOS result. Evidence: `artifacts/P15-M03-T019/t019-coveragefix-20261113T000422Z-8953700ad0ac4a669631569646bd5ab7-outcome-offline-diagnostic.json`.
- After the availability fix, 21/21 targeted integration cases passed, including early receipt, later receipt and later event-time guards; the warning-as-error App Release build, scoped format and actual hash-mismatch CLI check passed.
- The availability-fix observation used the same profile, policy and bounds; only the expired UTC start window, run ID and isolated paths changed. Its preflighted envelope is `artifacts/P15-M03-T018/t019-availabilityfix-20261113T010954Z-5df53543eb6a4dbd98c6b8392bd192b6/envelope.json`, SHA-256 `1bd8f5cba31e5b72257863142f5bd51185790b23267ab6e638e1d0b40e686458`; selected source and assembly evidence was frozen before launch, SHA-256 `55b1cb4913f04f9db5df8ece382129d2e50f9b7ab0fba452684fb676dc1a0aa1`.
- **Availability-fix result:** The run completed with exit 0 after the planned restart, observing 4/4 real 15m and 1/1 real 1h closes. All four decisions were NoSetup; signals, outbox rows and outcomes were zero. Independent replay verified all four prefixes and 1,831 causal input cuts, with identical live/restarted semantic SHA-256 `7b929da3fb9787a4903f0b16a53f57b4dc40aa3278d141ddacf8f88ee8facc12` and zero offline network calls. Restart took 0.2944884 seconds and graceful stop 0.2393461 seconds; 599 health samples recorded peak working set 107,003,904 bytes and peak logical store size 5,292,244 bytes. Evidence: `artifacts/P15-M03-T019/t019-availabilityfix-20261113T010954Z-5df53543eb6a4dbd98c6b8392bd192b6-causal-audit.json`.
- **Blocker before the extended run:** The fixed assembly had not completed a prospective run containing a genuine signal, local output and observed outcome. The earlier signal and real-bar offline diagnostic did not convert its failed run into a successful one. All attempts remain retained; the profile must not be tuned and a directional observation must not be presented as trading profit.
- On the user's continuation request, a further bounded public run was predeclared with unchanged profile, policy, resource bounds and selected source/assembly hashes; only the expired start window, run ID and isolated paths changed. Envelope `artifacts/P15-M03-T018/t019-continuation-20261113T051443Z-131fbfaacc6549b78660ee0cd03a4381/envelope.json` has SHA-256 `f1aeda9f1a6c3fbfdcb697147e9cc39c9f0d8a1ba5f6f5a98e666df74c7d6512`; preflight returned `ready` and pre-run evidence SHA-256 is `4f1c564fafe73653880493d38a0d1447f0f2762424087f622378b857b19a725d`. After the planned restart it stopped fail-closed on `public-stream-disconnect`, with quality `unknown`, one NoSetup diagnostic and no signals, outbox or outcomes; it is not accepted.
- An isolated offline recovery audit of that interrupted run checked its one causal prefix and 477 input cuts; restart parity passed with zero network calls and no outcomes created. The failed runtime remains unaccepted. Evidence: `artifacts/P15-M03-T019/t019-continuation-20261113T051443Z-131fbfaacc6549b78660ee0cd03a4381-failed-run-causal-audit.json`.
- One separately predeclared retry after that interruption used the same profile, policy, bounds and selected source/assembly hashes, changing only the expired UTC start window, run ID and isolated paths. Its envelope `artifacts/P15-M03-T018/t019-rerun-20261113T054414Z-6ba7ebd0f0ec4ef79b77ae902fb4c9ec/envelope.json` has SHA-256 `f6e1e46c4d8dea1a301bb90730ebfff6220a4bfad1fd1ab8b02bd40598b18db3`; preflight returned `ready` and pre-run evidence SHA-256 is `779f2ddb92c92d3a5a82a0c3c46234e2b62f30a7b0974a92fa294b630e21d701`.
- **Continuation retry result:** The run completed with exit 0 after actual restart, with 3/3 real 15m and 1/1 real 1h closes, three NoSetup diagnostics and zero signals, outbox or outcomes. Independent replay checked three prefixes and 1,439 causal input cuts; live and restarted semantic SHA-256 matched `3bf0fddd3ea29bc5592d06dc92f98dc7611ebef20fa72c8fe9d710fe9b771af1`, with zero offline network calls. Restart took 0.2834886 seconds and graceful stop 0.2291861 seconds; 366 health samples recorded peak working set 106,799,104 bytes and peak logical store size 5,198,697 bytes. Evidence: `artifacts/P15-M03-T019/t019-rerun-20261113T054414Z-6ba7ebd0f0ec4ef79b77ae902fb4c9ec-causal-audit.json`.
- **Earlier checkpoint:** The prior signal came from a failed run, and this completed run had no signal. At that point the required successful prospective chain had not yet been observed.
- A further bounded window was predeclared with the same profile, policy, resource bounds and selected source/assembly hashes; only the expired UTC start window, run ID and isolated paths changed. Its envelope `artifacts/P15-M03-T018/t019-window-20261113T061935Z-02ce6c7369784f55ae74732817fbc45f/envelope.json` has SHA-256 `a2dbe5dbcdc0fdfc6444653012e8a5da40d0fd7e7e0f2c7a725238d88ad7e1c6`; preflight returned `ready` and pre-run evidence SHA-256 is `191140df48293b41358274392e498886027f8eea010a94da33736aa1c4767dee`.
- **Additional bounded-window result:** The run completed with exit 0 after actual restart, observing 3/3 real 15m and 1/1 real 1h closes, three NoSetup diagnostics and zero signals, outbox or outcomes. Independent replay checked three prefixes and 1,450 causal input cuts; live and restarted semantic SHA-256 matched `70a17646de42fff44527f89b84e103cc5a2016b711d95f43a6612cf6f54723f8`, with zero offline network calls. Restart took 0.3144231 seconds and graceful stop 0.3234033 seconds; 482 health samples recorded peak working set 107,175,936 bytes and peak logical store size 5,262,505 bytes. Evidence: `artifacts/P15-M03-T019/t019-window-20261113T061935Z-02ce6c7369784f55ae74732817fbc45f-causal-audit.json`.
- **Earlier checkpoint:** This completed window also contained no signal. The earlier genuine signal was in a failed run, so the full prospective chain remained unverified until the extended run below.
- To avoid repeatedly restarting short windows, a single extended bounded public observation was declared before launch: maximum 14,400 seconds, at least 12 actual 15m closes and three actual 1h closes, with the same signal profile, scenario policy, memory/storage/freshness/retry limits and selected source/assembly hashes. This changed only software observation bounds, the expired start window, run ID and isolated paths; it is not an OOS comparison. Envelope `artifacts/P15-M03-T018/t019-extended-20261113T070421Z-3d1d1d99a6bc4d429d8b519b78e813c9/envelope.json` has SHA-256 `31bbe63b8240687e9d185f379e9fa73cc5e79b9c70fb0185f4ac61edb27b1761`; preflight returned `ready` and pre-run evidence SHA-256 is `10d081e6edd95830829d84aa2ae558ce24ce22d2eb619c3856c58c654d3bfec0`. The acceptance result is recorded at the top of this checkpoint.
- A 17-file selected-source hash manifest was captured after launch at `artifacts/P15-M03-T019/selected-source-hashes-t019-20261112T201038Z-3417cf1057c34e55b0b588257c5b41ea.json`, SHA-256 `514fae2dfc3e92c6d9a7d634426623aa56b2b697e4aaf8f69cdd98cc074a2f84`. It is not a complete build identity or evidence of predeclaration; the run declaration is designed to bind executing assembly hashes before public requests.
- The independent prefix/restart auditor was self-checked on the already frozen T017 real capture: all three causal prefixes matched independent replay; one committed signal and two diagnostics matched across restart, with zero network calls. This validates the audit procedure only and is not T019 prospective evidence.
- T019 changed: `SignalOnlyBoundedRun.cs`, `SignalOnlyBoundedPreflight.cs`, `SignalOnlyCommand.cs`, App test visibility; bounded SQLite ledger/journal and `SqliteBoundedStorage.cs`; bounded public REST body reader; focused Integration/Infrastructure/Binance tests and `docs/architecture/operations.md`. No orders, private execution, external publication or OOS comparison. No commit or push.

## P15-M03-T018 (SR-06.1) — DONE (2026-11-12)

- T014 and T017 were DONE before T018 began. The user authorized T018 and, only after its acceptance, T019. The separate public-only declaration and `signal-only preflight --envelope <file>` validate mandatory configuration, causal profile versions, limits, required drills, an active UTC start window, empty isolated storage, available disk space, and no private/order fields without opening stores or contacting an exchange.
- The write-once declaration is `artifacts/P15-M03-T018/t019-20261112T201038Z-3417cf1057c34e55b0b588257c5b41ea/envelope-v2.json`; its SHA-256 is `5a767cc4c501fb50a121d18ef4a812d3def33aa7de31b85bec3e6dfa8b1af68b`. The actual CLI preflight returned `ready`, configuration hash `8b903377c295185ecf82000687f4255ac203cd98ae41a349b1d7a3b49664aea2`, profile hash `3aabc0ab0fa393a18b91ae353d64054f60d4c1ac570fbfd6722df9f8aeef9a3b`, and scenario-policy hash `d7221f5953fa5e39641874a278ec00ed9687049feb201b14f58a022ba03f81f6`; the verdict is saved beside the envelope.
- The declaration selects BTCUSDT, 15m/1h and the exploratory v4 profile. It requires a fresh public exchange-info eligibility check, at most 70 minutes, 64 MiB store budget, 512 MiB working-set budget, 50,000 receipts, explicit REST/WS/reconnect/time limits, three actual 15m closes and one 1h close, and local journal/console outputs. It requires disconnect, gap, database-failure, restart and graceful-stop drills, restart parity and real observations only. NoSetup is valid; Unknown cannot become Ready. These are software-run bounds, not trading thresholds or performance claims.
- Validation: 11 focused preflight/composition cases passed, including missing/inconsistent declarations, forbidden private settings, cancellation without store creation, existing-store protection and mandatory drill coverage. The App Release build passed with zero warnings/errors and scoped C# format verification passed. An earlier draft was rejected because its history timestamp was not UTC; it remains preserved and was not used.
- **Acceptance boundary:** `ready` validates the envelope and current preflight conditions only. It does not enforce runtime, storage, or freshness bounds and does not prove exchange eligibility or a running service. T019 must implement and verify those behaviors; the existing `signal-only --config` command is unbounded and is not a substitute. No T019 run was started during T018, and no orders, private execution, external publication or OOS comparison occurred.
- Changed: `src/TradingBot.App/SignalOnlyBoundedPreflight.cs`, `SignalOnlyCommand.cs`, `SignalOnlyConfiguration.cs`; `tests/TradingBot.IntegrationTests/SignalOnlyBoundedPreflightTests.cs`, `NativePublicKlineFeedTests.cs`; `docs/architecture/operations.md`, T018/T019 status/evidence in `docs/TASKS.md`, and this handoff. Unrelated working changes remain untouched.

## P15-M03-T017 (SR-05.3) — DONE for software reporting (2026-11-12)

- Started only after T016 was accepted as DONE; T008 was DONE. Added Backtesting `SignalOpportunityReporter` and App `signal-only opportunity-report` for a manifest-bound, read-only eligible 15m universe, explicit no-signal/blocked/unknown/overlap/unmatched cells, denominators, coverage by historical/prospective availability model, directional metrics, publication delays and observer bounds. MA-only and price-volume controls are marked not evaluated; hypothetical net expectancy is null without usable cost/latency/funding/non-fill inputs. No OOS comparison.
- Verification: focused `SignalOutcomeObserverTests` including report boundary metrics 6/6, committed signal/outcome journal smoke 1/1, App Release build `--warnaserror` zero warnings/errors and scoped format verification passed. Two actual CLI runs over the T008 BTCUSDT capture `20261110T061340Z-6600a18e4692496aacde0de5e2bbc4a4` and a write-once manifest bound to its real ledger/PIT/config hashes produced byte-identical canonical report SHA-256 `39ddc8c16a469adb68936377dd38167bc645c66ff9564c7cfa6ae3d8d1789554`: 99 expected, 99 covered, 99 Unknown decision rows, zero inferred NoSignal or directional labels.
- Initial blocker: the T008 capture had no previously declared complete 15m/1h signal profile or matching real committed journal. On explicit user authorization, a new exploratory profile and scenario policies were frozen before offline replay; they were not predeclared when T008 was captured. Publication receipts, outcomes, labels and usable cost/latency/funding inputs remain unavailable and Unknown.
- The T008 ledger contains 99 accepted 15m and 25 accepted 1h inputs. Coverage is 96/96 15m and 24/24 1h with modeled historical REST availability, plus 3/3 prospective 15m and 0/0 prospective 1h intervals with recorded WebSocket availability. Historical REST receipts are warmup, not retrospective signal decisions.
- Offline replay of the actual T008 inputs used the frozen profile, committed three `signal-evaluation-unknown` diagnostics, and produced no signals or outbox entries. Every retained input respected the original trigger's event/close/availability/sequence cut; a restart preserved journal semantics and bytes, with zero network attempts.
- Follow-up: fixed covered-interval frequency to remain null with `unresolved-covered-intervals` when any covered decision is Unknown. A committed diagnostic now must match the captured bar revision; mismatch fails closed. Focused regression fixtures verified both fixes. Two fresh CLI runs on the real T008 capture and new write-once diagnostic manifest produced identical canonical bytes, SHA-256 `240283581fee9a4a91a6c22195341a8faf975757c854da4e47bb8a56a412d972`: 99 expected, 99 covered, 99 Unknown, 0 NoSignal, 99 exclusions, null frequency and null hypothetical net expectancy. The manifest and both reports are retained under `artifacts/P15-M03-T017/9be024672c1e4cb78392923d9d99d113/`; its configuration hash describes the T008 capture, not a signal profile.
- The report binds the signal-profile, scenario-policy, capture, input-ledger, manifest, predeclared-run and frozen-journal hashes; accepted signal/diagnostic provenance is checked against captured revisions. Scenario-specific diagnostics are associated with their source candle, including multiple reasons; conflicting revisions fail closed. The binding records content identity, not independent proof of declaration time.
- Two final actual CLI runs over that frozen T008 journal and manifest produced identical output bytes (including the CLI newline), SHA-256 `78ae7627c05763149a072a3728bab385c2e269ece1d5786c2fced57a39dfa164`; the canonical JSON alone hashes to `59ae8517f786b480db0010957756175cde0f090a75a854b77d69966fe9619b5f`. The report records 99 expected/covered, 99 Unknown, 0 NoSignal, 0 signals, 99 exclusions, null frequency and null hypothetical net expectancy. Results and freeze records are under `artifacts/P15-M03-T017/research-t008-c0f7b3bc869f459583f7df9975e09488/`.
- A separately authorized public-only extension used a profile and policies declared before capture, with unchanged numerical settings. Actual capture `20261112T184159Z-107922243e1b4a54b7b867df9c41850f` covered historical REST 338/338 15m and 84/84 1h, and prospective WS 2/2 15m and 1/1 1h. Three 15m close receipts were accepted; the third arrived just before its nominal end and is retained as one unmatched public input, not counted in the expected window. This capture is not a T019 run.
- A new write-once manifest (`c3b55e58b273533122bf147a02d175e161e9703ddca0ce83dc58ff53bc61f127`) was frozen before offline replay. The real input replay produced three committed Unknown diagnostics, zero signals/outbox and zero network attempts; all input cuts were causal and restart preserved semantic state and journal SHA-256 `1f3047c07e5d171af679b9260be4e3a5ded20c2105bd01f87b5c89872109aab4`. No publication or outcome was created.
- Two CLI runs over that frozen manifest and journal were byte-identical; canonical JSON SHA-256 `77bde54d5edcd25065e4558899124b1dbf857d14518df6c45588fdcbb7a5cd5c`. Denominators: 340 expected/covered, 340 Unknown, 0 NoSignal, 0 signals, 340 exclusions; frequency and hypothetical net expectancy are null. The reports and freeze evidence are in `artifacts/P15-M03-T017/research-20261112T184159Z-107922243e1b4a54b7b867df9c41850f/`.
- The initial v2 blocker was permanent `UnknownWarmup` after a pivot without confirmation-time ATR. The separately versioned `price-context-v3` conservatively tracks possible level breaks and role reversals; it recovered setup structure but the captured HTF remained Unknown. Its distinct frozen manifest and report are retained under `artifacts/P15-M03-T017/research-v3-20261112T184159Z-107922243e1b4a54b7b867df9c41850f/`.
- `price-context-v4` retains correlated alternative level pairs instead of merging uncertain slots independently. It reports Ready only when all possible branches converge to one fully known pair and requires fresh HTF confirmation. Existing v2/v3 journal reopen/restart remained byte-identical with zero network calls. The v4 formula and profile are exploratory changes informed by previous observations; they were frozen before the v4 replay, not before the original data capture, and are not OOS evidence.
- The v4 manifest `46c8e103de18ae9bbeed4c2ba908ea0c52ea2bfa081e84ca9d4e6afb95e35fa8` was frozen with profile and assembly hashes before offline replay. Three genuine captured triggers yielded one committed signal and output-only outbox row, plus two committed NoSignal diagnostics; no external delivery, publication or outcome was created. Each input obeyed its event/close/availability/sequence cut; restart preserved journal SHA-256 `ac9de9630423ecfc216ba8be2df454022624fb32406328400bfa62078cab471b` and semantic state with zero network attempts.
- Two actual CLI reports over that frozen journal and manifest had identical canonical JSON SHA-256 `cb74a791e3c350ef2085554e41691e57bb5da09d9ba69a4971efe41c5100b6f2`: 340 expected/covered, 338 Unknown historical warmup cells, 1 genuine NoSignal, 1 signaled cell, 338 exclusions and 1 unmatched public input. Frequency and hypothetical net expectancy remain null; the third captured trigger is outside the declared coverage window. The manifest remains Unknown for undeclared labels/costs/criteria; no performance or strategy-validation verdict is implied.
- Acceptance: T017 software criteria passed. Focused Core feature/setup/quality regressions passed 62/62, consumer tests 5/5, report integration tests 9/9, and outcome/report metric tests 7/7; affected Release builds passed with zero warnings/errors and scoped C# format verification passed. Docs fixtures passed 11/11. Evidence is under `artifacts/P15-M03-T017/research-v4-20261112T184159Z-107922243e1b4a54b7b867df9c41850f/`. T018 may now start separately; no T019, orders, private execution or OOS comparison was started during T017.
- Changed in T017: `src/TradingBot.Backtesting/SignalOpportunityReport.cs`; `src/TradingBot.App/SignalOpportunityReportCommand.cs`, `SignalOnlyCommand.cs`, `SignalOnlyInstrumentConsumer.cs`, `NativePublicKlineFeed.cs`; Core public-profile, price-context and quality/setup version guards; focused Core/Replay/Integration tests; `docs/architecture/research-freeze.md`, `public-signal-profile.md`, `price-context-features.md`, the T017/T018 task statuses and this handoff. Unrelated working changes remain untouched.

## P15-M03-T016 (SR-05.2) — DONE (2026-11-12)

- T015 and T010 were DONE. T016's dependency-only BLOCKED text was stale; no additional substantive blocker was found. The user explicitly authorized T016 then T017; no T017 implementation started before this T016 status/evidence checkpoint.
- Added `SignalOutcomeObserver` and `SignalOutcomeJournal` in Backtesting. Decision and named-channel published boundaries are independent; bars containing publication are excluded, and published returns start from the next eligible open, not the decision reference. Available complete bars, horizon/expiry/invalidation, missing path, censoring, same-bar first-passage ambiguity/bounds or a verified detailed order, and overlap identities are retained without fill claims. Isolated atomic write-once outcomes deduplicate on reopen and refuse changed paths.
- Verification: `SignalOutcomeObserverTests` 5/5; `CommittedSignalOutcomeSurvivesObserverJournalReopen` 1/1 with a committed SQLite signal journal and separate outcome journal; Backtesting Release build `--warnaserror` passed with zero warnings/errors. Scoped C# formatting applied. Synthetic local fixtures only, not market performance or historical publication evidence.
- Changed: `src/TradingBot.Backtesting/SignalOutcomeObserver.cs`, `SignalOutcomeJournal.cs`; `tests/TradingBot.Replay.Tests/SignalOutcomeObserverTests.cs`; `tests/TradingBot.IntegrationTests/SignalOnlyJournalOutputTests.cs`; `docs/architecture/research-freeze.md`; T016 row in `docs/TASKS.md`; this handoff. No commit/push or changes to unrelated untracked scenario files.
- T017 remains BLOCKED until the next separately recorded acceptance; the user authorized its implementation after T016 acceptance. Its row still has stale dependency-only wording, which is deliberately not edited under T016.

## P15-M03-T013 (SR-04.2) — DONE (2026-11-12)

- **Authorization/order:** User authorized T013 only, followed by T014 after acceptance. T012 was DONE. T014/T016+ and all other tasks remained untouched during T013; unrelated working-tree changes were preserved. No commit or push.
- **Deliverable:** Added `TelegramOutboxDeliveryService` using only committed `SqliteSignalJournalStore` rows and fixed Bot API `sendMessage` plain-text output with stable outbox ID; no polling, updates, buttons, or inbound controls. Added a separate durable `SqliteSignalOutboxDeliveryStore` with persisted policy, claims/leases, pre-send attempts, immutable receipts, bounded attempts/age/backoff, sender-wide `retry_after`, expiry, permanent failure, and terminal `UnknownDelivery` after possible acceptance or expired sending lease. Telegram `message_id` is an API acknowledgment, not a human-read receipt.
- **Checks:** `TelegramOutboxDeliveryTests` passed 8/8 using local deterministic SQLite/HTTP-handler fixtures: claim recovery, crash/reopen, timeout-after-accept, acknowledgment/reopen, exact rate-limit delay, permanent failure, retry exhaustion, expiry, malformed response, and redaction. App Release build with `--warnaserror` passed with 0 warnings/errors. Scoped C# format verification passed. No real credentials and no external Telegram request.
- **Docs validator/stale blockers:** `tools/validate-docs.ps1` stops at `P15-M03-T014 is BLOCKED with all dependencies DONE but has no explicit unresolved blocker`. A read-only scan using the validator's rule also finds `P15-M03-T016` for the same reason (dependencies T015 and T010 are DONE). Both task rows remain unchanged per scope.
- **Boundaries:** No orders, private execution, risk, intents, positions, or FINAL_TECH_SPEC changes. `docs/architecture/persistence.md` documents the isolated delivery ledger. Only T013 status/evidence and the current summary were updated in `docs/TASKS.md`.
- **Changed files:** `src/TradingBot.Infrastructure/SignalOutboxDeliveryContracts.cs`, `SqliteSignalOutboxDeliveryStore.cs`; `src/TradingBot.App/TelegramOutboxDeliveryService.cs`, `SignalOnlyCommand.cs`; `tests/TradingBot.IntegrationTests/TelegramOutboxDeliveryTests.cs`; `docs/architecture/persistence.md`, `docs/TASKS.md`, and `HANDOFF.md`.

## P15-M03-T014 (SR-04.3) — DONE (2026-11-12)

- **Authorization/order:** T014 began only after T013 was accepted as DONE. No T016+ or other task was started.
- **Local output:** `SignalOnlyJournalOutputTests` passed 2/2 on a committed SQLite fixture. This verifies local rendering only.
- **API acknowledgment:** `ApiAcknowledgmentAndMessageIdSurviveDeliveryJournalReopen` passed 1/1 with a local fake HTTP handler; the Telegram-style `message_id` persisted across reopen. This is fixture evidence only—not an external receipt and not proof of human reading.
- **Prior local verdict:** Before separate send authorization, local rendering and fake API acknowledgment had no external receipt. T014 remained BLOCKED.
- **Docs validator:** After genuine T014 acceptance, `tools/validate-docs.ps1` fails on the out-of-scope stale blocker `P15-M03-T016 is BLOCKED with all dependencies DONE but has no explicit unresolved blocker`. Its row remains unchanged.
- **One-shot external smoke:** After explicit confirmation for one message to the previously specified personal chat, a synthetic committed `TESTONLY` signal (`signalId=7e624eed95e07643b06d457d90efc9f5cdd9b675de6e52bef5bae1296b21c25d`, `outboxId=60b7944f3219eda1ab5028df7898ed92a186e1b75511394104bf64e722e7e0c3`) rendered locally as one pending output with `publishedAt=null`. The previously supplied token and chat ID were supplied only as child-process environment variables without writing their values to source, journal, or output. One `sendMessage` attempt returned `PermanentFailure`; no second request was made.
- **First external verdict:** Reopening the first isolated delivery database showed `PermanentFailure`, one started attempt, and no API acknowledgment receipt. The exact provider error was not retained by the redacted sender; no retry of that terminal row occurred. At that point T014 remained BLOCKED.
- **Redaction check:** Four retained SQLite journal/sidecar files were scanned in memory without displaying the credential or destination; neither value was present. The temporary runner source and build artifacts were removed; only the synthetic redacted journals remain under the temporary smoke directory.
- **Cause and recovery:** Read-only Telegram `getMe` returned HTTP 200; `getChat` for the previously specified personal chat returned 400 (`chat-not-found`). After the user started the bot conversation from their account, `getChat` returned HTTP 200. No inbound polling or order controls were added.
- **Accepted external smoke:** A new synthetic committed `TESTONLY` signal (`signalId=ef06960991bca558d99338a4ce363a5598da558af306ae3f53940714f8648c56`, `outboxId=10d54e8f3af92c185d18e0e05955fccfd245d8f08cc04e48be74c13d14a5505b`) rendered as one pending local output with `publishedAt=null`. After a separate exact-message/chat confirmation, one `sendMessage` request returned `Acknowledged`; reopening the delivery database showed `Acknowledged`, one attempt, and a persisted Telegram API message receipt. The local renderer still made no publication claim; API acknowledgment does not prove human reading or a real-market signal.
- **Redaction and scope:** Four files of the successful synthetic run were scanned in memory; neither credential nor chat ID appeared. The temporary runner source/build files were deleted; redacted SQLite journals remain in the temporary smoke directory. T014 status/evidence was updated only after this authorized receipt. T016+ and other task rows remain unchanged.



## P15-M03-T015 (SR-05.1) — DONE (2026-11-12)

- **Authorization/order:** T012 was accepted and its status/evidence and HANDOFF updated before T015 began. The user authorized T015 next and excluded T013–T014, T016+, and all other tasks. No excluded task row or implementation was changed.
- **Deliverable:** Added BCL-only `SignalStudyManifest` and `SignalStudyTemporalSplitPlan` in Backtesting. The canonical identity binds dataset, PIT universe, configuration, formula, seed history, labels, horizons, cost, latency, exclusions, OOS dataset, evaluation criteria, split plan, and trial registry. Missing required declarations remain `Unknown`; no numeric threshold is invented. Half-open chronological folds validate information cuts, label availability, overlap/purging, and availability-derived forward embargo. A write-once manifest journal binds evidence to identity; an atomic OOS first-view journal blocks reuse of the same OOS dataset hash under a changed study identity.
- **Authority/limits:** These contracts do not evaluate a strategy or authorize promotion, orders, risk, positions, private transport, or fills. Tests use synthetic fixtures; no real OOS performance evidence is claimed.
- **Checks:** `SignalStudyManifestTests` passed 4/4, covering canonical immutable snapshots/write-once persistence, changed-input invalidation, explicit Unknown gates, temporal boundaries and development/validation embargoes, plus OOS first-view idempotency/reuse blocking. Scoped C# format verification passed. `tools/validate-docs.ps1` remains blocked by `P15-M03-T013 is BLOCKED with all dependencies DONE but has no explicit unresolved blocker`; T013 is unchanged because it is outside this authorization.
- **Changed files:** `src/TradingBot.Backtesting/SignalStudyManifest.cs`, `SignalStudyTemporalSplitPlan.cs`, `SignalStudyOutOfSampleViewJournal.cs`; `tests/TradingBot.Replay.Tests/SignalStudyManifestTests.cs`; `docs/architecture/research-freeze.md`, `docs/TASKS.md`, and `HANDOFF.md`.

## P15-M03-T012 (SR-04.1) — DONE (2026-11-12)

- **Authorization/order:** The user authorized T012 first and T015 only after T012 acceptance. T013–T014 and T016+ remain outside this authorization. T012 status/evidence is recorded before starting T015.
- **Deliverable:** Added a read-only `signal-only render --journal <file> --symbol <symbol> --format <json|console>` path. JSON and console render committed signal/evidence identity, interval, decision/HTF cuts, scenario anchor/episode/event links, reference provenance, quality reasons, and separate `RESEARCH`/`UNKNOWN` semantics. Missing configuration is a diagnostic; the view does not invent a delivery receipt or publication time.
- **State/authority:** Scenario references are explicitly `scenario-reference-only`; `ConfirmedOnClosedBar` remains distinct from statistical validation `UNKNOWN`. Duplicate append is idempotent, continuation/cancel/expiry preserve the original signal and alert IDs, and cross-family overlap preserves both signal/alert links. Rendering uses SQLite read-only mode; no Telegram sender, order, private transport, risk, intent, or position path was introduced.
- **Checks:** Process-level App CLI JSON/console tests on a committed SQLite fixture passed 2/2, including missing/unknown configuration, duplicate/retry/continuation, overlap, cancel, and expiry. Read-only journal test passed 1/1. Scoped changed-file format verification passed. The docs fixture/validator is blocked because T013 is BLOCKED with all dependencies DONE and no explicit unresolved blocker; T013 remains unchanged per scope. Canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- **Limits:** The committed journal was a deterministic fixture, not a live exchange capture. No external delivery or production operation was attempted.
- **Changed files:** `src/TradingBot.App/SignalOnlyCommand.cs`, `SignalOnlyJournalRenderer.cs`; `src/TradingBot.Infrastructure/SqliteSignalJournalStore.cs`; `tests/TradingBot.Infrastructure.Tests/SqliteSignalJournalStoreTests.cs`; `tests/TradingBot.IntegrationTests/SignalOnlyJournalOutputTests.cs`; `docs/architecture/signal-contracts.md`, `docs/TASKS.md`, and `HANDOFF.md`.

## P15-M03-T011 (SR-03.3) — DONE (2026-11-10)

- **Authorization/order:** T010 was formally accepted as DONE before T011 began. The user authorized only T010 and T011 in sequence. T012+ remain BLOCKED pending separate authorization; no later task was started or unblocked.
- **Deliverable:** Added `SignalOnlyConfiguration`, `SignalOnlyCommand`, and `SignalOnlyInstrumentConsumer`, and routed `signal-only --config` through `Program.Main` without changing the existing `shadow`/`shadow-audit-export` paths. Config is strict format v1 and `signal-only-public`: it requires an explicit ready Binance USD-M native-kline profile and scenario policies, unique in-bound symbols, and distinct file-backed input-ledger/signal-journal paths. Public REST warms an empty ledger; restart restores an existing ledger, replays pending accepted closed 15m receipts in original durable order, and resumes with one serialized consumer per instrument.
- **Signal flow:** Closed 15m WebSocket inputs use the existing native feed, Lane A evaluators, scenario projection, and quality. Confirmed Center Retest produced one durable signal/evidence record, scenario alert, checkpoint, and outbox item in SQLite. The signal's 1h provenance hash matched the separately persisted public input receipt; this exposed and fixed receipt-hash tracking for non-trigger HTF WebSocket inputs. Non-confirmed input produced a durable diagnostic; restart/retry and crash-gap replay did not duplicate it. No evaluator/formula copy was added.
- **Boundaries:** Unknown/duplicate/forbidden config is rejected before stores open. The actual process-level `signal-only --config` probe rejected an unknown `privateTransport` field with exit 2. Reflection checks found no private signer/executor, risk authorization, OrderIntent, position, or portfolio dependency in the composition. No order, execution, risk/position behavior, private transport, or delivery side effect was added. The existing shadow command remains intact.
- **Checks:** `NativePublicKlineFeedTests` passed 22/22; `OperationalShadowCommandTests` passed 2/2; App Release build with `--warnaserror` passed with 0 warnings/errors; scoped changed-file format verification passed. Documentation fixtures passed all 11 cases; the docs validator passed with 19 phases, 43 milestones, 152 tasks, and no READY task (explicit unresolved blockers). Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- **Limits:** Positive closed-input/journal behavior used deterministic public-feed fixtures with real SQLite; no live Binance session, Linux/systemd execution, or production operation was exercised.
- **Changed files:** `src/TradingBot.App/SignalOnlyConfiguration.cs`, `SignalOnlyCommand.cs`, `SignalOnlyInstrumentConsumer.cs`, `NativePublicKlineFeed.cs`, `Program.cs`; `tests/TradingBot.IntegrationTests/NativePublicKlineFeedTests.cs`; `docs/architecture/operations.md`, `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and `HANDOFF.md`.

- **Next:** P15-M03-T012 and P15-M03-T015 are now DONE after separate authorization. T013–T014 and T016+ remain outside scope; do not proceed without separate authorization.


## P15-M03-T010 (SR-03.2) — DONE (2026-11-10)

- **Authorization/dependencies:** The user authorized T010 after formal P15-M03-T009=DONE and authorized T011 only in strict sequence. T010 acceptance was recorded before T011 implementation. No T012+ work was started or authorized.
- **Deliverable:** Added `SqliteSignalJournalStore` and immutable commit/read contracts in Infrastructure. Standalone SQLite schema v1 persists input receipts, stable signal/evidence identity, append-only scenario events, diagnostics, outbox rows, and checkpoint. A single immediate transaction commits the complete projection; exact retry returns false, identity/hash conflict fails closed, and a late event cannot rewrite the committed signal. Shared order/portfolio storage is not opened.
- **Behavioral checks:** `SqliteSignalJournalStoreTests` passed 8/8: commit/reopen/exact retry, late-input stability, input and signal hash conflicts, checkpoint-trigger failure rollback and retry, concurrent exact commits, continuous/restarted snapshot parity, and standalone-schema isolation. The full Infrastructure.Tests project passed 126/126.
- **Build/format:** Infrastructure Release build with `--warnaserror` passed with 0 warnings/errors. Scoped whitespace verification passed for both new Infrastructure files and the journal test.
- **Changed files:** `src/TradingBot.Infrastructure/SignalJournalContracts.cs`, `src/TradingBot.Infrastructure/SqliteSignalJournalStore.cs`, `tests/TradingBot.Infrastructure.Tests/SqliteSignalJournalStoreTests.cs`, `docs/architecture/persistence.md`, `docs/TASKS.md`, and `HANDOFF.md`.

```powershell
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SqliteSignalJournalStoreTests'
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" build src/TradingBot.Infrastructure/TradingBot.Infrastructure.csproj -c Release --no-restore --warnaserror'
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore'
```


## P15-M03-T009 (SR-03.1) — DONE (2026-11-10)

- **Authorization/dependencies:** The user authorized T009, then separately authorized only T010 and T011 in strict sequence. T008 was DONE before T009; T009 was formally DONE before T010 started. T012+ remain unauthorized.
- **Deliverable:** Added `LaneASetupObservationProjection`, retaining the exact existing SETUP-01/02 results and their shared price-context/quality evidence. Each family/direction exposes `Confirmed`, `NoSetup`, `Unknown`, or `Invalidated`; `IsComplete` is false if any required family/direction is unknown. Same-direction overlap is explicitly `Confirmed`, `NoOverlap`, or `Unknown`, preserving both families. `LaneASignalEvaluationResult.SetupObservations` exposes the projection. No evaluator/formula clone, optional Lane B/C gate, `Neutral` state, persistence, order, risk, position, or private transport behavior was added.
- **Behavioral checks:** The focused Core filter below passed 43/43, covering SETUP-01/02 and quality regressions, all four statuses in both directions, family overlap, required-unknown blocking, optional unknown Vwap/POC, unknown-to-confirmed prefix transition, and same-input serialized replay parity.
- **Build/format:** `TradingBot.App` Release build with `--warnaserror` passed with 0 warnings/errors. Scoped whitespace verification passed for the Core projection, App evaluator result, and projection test. The App file was already untracked; its existing line endings were normalized because the changed-file formatter requires CRLF.
- **Docs/hash:** Documentation fixtures passed 11/11; the validator passed with 19 phases, 43 milestones, 152 tasks, and no READY task (explicit unresolved blockers). Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- **T010 boundary:** T010 was subsequently authorized and accepted as documented above; T011 was not started before T010 was formally marked DONE. No T012+ implementation was authorized or started.
- **Changed files:** `src/TradingBot.Core/LaneASetupObservationProjection.cs` (new), `src/TradingBot.App/LaneASignalEvaluator.cs` (additive projection property), `tests/TradingBot.Core.Tests/LaneASetupObservationProjectionTests.cs` (new), `docs/architecture/lane-a-observation-projection.md` (new), `docs/TASKS.md` (T009 status/evidence; T010 blocker evidence only), and `HANDOFF.md`.

```powershell
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LaneASetupObservationProjectionTests|FullyQualifiedName~BoundaryRejectionSetupTests|FullyQualifiedName~CenterRetestSetupTests|FullyQualifiedName~LaneASetupFixtureTests|FullyQualifiedName~FeatureQualityEvaluatorTests|FullyQualifiedName~FeatureQualityRegressionTests"'
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" build src/TradingBot.App/TradingBot.App.csproj -c Release --no-restore --warnaserror'
pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1
pwsh -NoProfile -File tools/validate-docs.ps1
pwsh -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'
```


## P15-M03-T006–T008 authorized work

- **Authorization/order:** The user authorized only T006 → T007 → T008; T007 and T008 are formally DONE. T008's predeclared envelope was BTCUSDT only via `/fapi/v1/exchangeInfo` plus `MetadataCandidateFilter`; REST `[last completed UTC hour boundary − 24h, boundary)`, 96/24 expected closes, one REST page per timeframe and raw caps 97/25 per run; prospective WS ≤70m, one controlled reconnect, no automatic retry, ≤120s to first post-reconnect event; 64 MiB per-run store, 50,000 receipts, 4 MiB metadata/report each, WS frame/message ≤64/256 KiB, dedup 512. Three separate bounded attempts were made: two aborted at WebSocket parsing and one passed. Each attempt used one page per timeframe, so total across attempts was three pages per timeframe; no REST retry occurred within a run. T009+ remains blocked and unauthorized. P17, private execution, order/risk behavior, canonical source, commit, and push remain out of scope.
- **T006 / SR-02.3 — DONE:** `TradingBot.App.NativePublicKlineFeed` composes the existing REST client, reconnecting market WebSocket client, and T005 timeframe boundary; no parallel Binance client or transport was added. It warms native 15m/1h history from declared starts, evaluates only a newly accepted closed 15m input, uses the latest eligible closed 1h cut, and blocks readiness for missing seed/warmup, gaps, stale inputs, or unresolved stream diagnostics. Official Binance REST catalog/schema and WebSocket connection/route-change pages were checked before the adapter route change. The default `/market` path returned 404 in the bounded public smoke; the existing market client now uses the documented `/market/ws` route. A real public BTCUSDT 15m WebSocket close was accepted by the temporary smoke (1/1); it was not represented as a long run or market coverage.
- **T007 / SR-02.4 — DONE:** `SqliteNativeBarInputLedger` is a file-backed append-only bar-input ledger with its own schema version, monotonic per-symbol sequence, durable payload hash/status/diagnostic receipts, and atomic committed-trigger checkpoint. It refuses non-empty or unsupported databases and never migrates the shared trading database. The feed replays receipts through the T005 boundary before REST warmup, restores seed/history and sequence after restart, and exposes explicit future-only `BackfillAsync`; corrections remain diagnostic and do not replace accepted revisions or rewrite committed evaluations. There is no retention/deletion API. SQLite failures propagate and fail the feed closed. No existing capture row was marked L2.
- **Focused checks:** `SqliteNativeBarInputLedgerTests` 3/3; `NativePublicKlineFeedTests` 14/14. The transaction failure fixture proves receipt/checkpoint rollback; the restart fixture proves history restore without REST refetch, no duplicate trigger, and evaluation parity with an uninterrupted replay.
- **Final affected Release checks:** `TradingBot.App` build with `--warnaserror` passed with 0 warnings/errors; Infrastructure.Tests passed 118/118; IntegrationTests passed 187/187. Scoped `dotnet format ... whitespace --verify-no-changes --no-restore` passed for all changed C# files.
- **Docs/hash:** validator fixtures passed all 11 cases; the native docs validator passed with `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. Canonical source hash verification returned `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

```powershell
powershell -NoProfile -Command '$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"; & $dotnet build src/TradingBot.App/TradingBot.App.csproj -c Release --no-restore --warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet test tests/TradingBot.IntegrationTests/TradingBot.IntegrationTests.csproj -c Release --no-restore; exit $LASTEXITCODE'
powershell -NoProfile -Command '$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"; & $dotnet format src/TradingBot.App/TradingBot.App.csproj whitespace --include src/TradingBot.App/NativePublicKlineFeed.cs --verify-no-changes --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet format src/TradingBot.Infrastructure/TradingBot.Infrastructure.csproj whitespace --include src/TradingBot.Infrastructure/SqliteNativeBarInputLedger.cs --verify-no-changes --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet format tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj whitespace --include tests/TradingBot.Infrastructure.Tests/SqliteNativeBarInputLedgerTests.cs --verify-no-changes --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet format tests/TradingBot.IntegrationTests/TradingBot.IntegrationTests.csproj whitespace --include tests/TradingBot.IntegrationTests/NativePublicKlineFeedTests.cs --verify-no-changes --no-restore; exit $LASTEXITCODE'
powershell -NoProfile -Command '$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"; & $dotnet test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteNativeBarInputLedgerTests"; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & $dotnet test tests/TradingBot.IntegrationTests/TradingBot.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~NativePublicKlineFeedTests"; exit $LASTEXITCODE'
powershell -NoProfile -Command '& pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & pwsh -NoProfile -File tools/validate-docs.ps1; exit $LASTEXITCODE'
powershell -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'
```

- **T008 / SR-02.5 — DONE:** Final successful run `20261110T061340Z-6600a18e4692496aacde0de5e2bbc4a4` is in `artifacts/P15-M03-T008/`. PIT BTCUSDT eligibility passed; REST `[2026-11-09T06:00Z, 2026-11-10T06:00Z)` covered 96/96 15m and 24/24 1h bars with one page per timeframe. WS ran 46m14s (`06:13:45Z`–`06:59:59Z`); one reconnect completed with first event after 1.859s. The report records 3/3 prospective 15m intervals, zero gaps/corrections/unknowns/diagnostics, and one accepted closed 1h input after reconnect. That 1h bar is durable and present in replay (`bar:v2:BINANCE:USD-M-PERPETUAL:BTCUSDT:1h:1794290400000`); its prospective 1h expected/covered arrays are empty because the run ended just before the nominal 07:00 boundary, so no full prospective-hour coverage is claimed. SQLite reopen recovered all 9,805 receipts with zero REST refetch; 124 replay IDs and history/replay hashes match before/after restart. `coverage-report.json` was reopened and its report hash verified. Final run store: 11,241,979 bytes; all three attempt artifacts: 13,925,691 bytes. Two earlier attempt reports remain in their run directories as failed evidence; final passing evidence and hashes are recorded in `docs/TASKS.md`.
- **T008 validation:** `dotnet run --project tools/TradingBot.T008Capture/TradingBot.T008Capture.csproj -c Release --no-build` — PASS; Exchange.Tests 71/71; IntegrationTests 190/190; capture-tool Release build `--warnaserror` — 0 warnings/errors; changed-file format verification — PASS; validator fixtures 11/11 and documentation validator — PASS. Whole-solution format verification was attempted and failed only on pre-existing line endings in unrelated `src/TradingBot.App/LaneASignalEvaluator.cs`; that file was not changed. Canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- **Canonical source:** SHA-256 `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; unchanged.
- **Changed files:** `TradingBot.slnx`; `src/TradingBot.App/NativePublicKlineFeed.cs`, `src/TradingBot.Infrastructure/SqliteNativeBarInputLedger.cs`, `src/TradingBot.Exchange.Binance/BinanceMarketWebSocketClient.cs`, `src/TradingBot.Exchange.Binance/BinanceWebSocketContracts.cs`; `tests/TradingBot.Exchange.Binance.Tests/BinanceWebSocketClientTests.cs`, `tests/TradingBot.Infrastructure.Tests/SqliteNativeBarInputLedgerTests.cs`, `tests/TradingBot.IntegrationTests/NativePublicKlineFeedTests.cs`; `tools/TradingBot.T008Capture/TradingBot.T008Capture.csproj`, `Program.cs`, `CaptureEvidence.cs`, `T008CaptureRunner.cs`; `docs/architecture/market-data.md`, `docs/architecture/persistence.md`, `docs/TASKS.md`, `HANDOFF.md`.


## P15-M03-T005 (SR-02.2) — DONE (2026-11-10)

- **Authorization/dependencies:** User authorized only P15-M03-T005. P15-M03-T004 and P04-M01-T003 were DONE before implementation. P15-M03-T006 implementation remains unauthorized; after explicit user approval, only its blocker text was clarified, with status left BLOCKED.
- **Deliverable:** Added `BinanceKlineIdentity` format 2 with venue `BINANCE`, product `USD-M-PERPETUAL`, canonical symbol, timeframe, and open time; new Core event IDs are v2 and distinguish 15m/1h at the same open. Existing historical IDs are not rewritten. `BinanceKline` now retains `timeframe` and Binance's raw inclusive `T`; ingestion separately exposes `InclusiveCloseTime` and `EndExclusiveTime`. Native 15m/1h UTC intervals require nonnegative representable open on the Unix UTC grid, exact duration, and closed input. Invalid conditions return diagnostic results without a Core `Bar`.
- **Revision rule:** The versioned SHA-256 rule (`binance-usdm-kline-revision-v1`) hashes open and inclusive close times, exact-decimal OHLC/base volume/quote volume/taker-buy volumes, and trade count; it excludes `Ignore`, source metadata, and transport event time. Equal identity+hash is the only REST/WS duplicate rule: no second Core Bar, source metadata is retained. Equal identity with a different hash returns `RevisionConflict` and keeps the prior accepted revision. REST callers pass the prior accepted-revision set; WebSocket callers pass the matching accepted revision. This is a boundary, not a durable ledger; persistence/restart state remains future work.
- **Official schema review (2026-11-09):** Checked the requested [current USD-M REST schema URL](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Kline-Candlestick-Data) before changing the adapter; it rendered only the current documentation shell. The current [official machine-readable catalog](https://developers.binance.com/en/docs/llms-full.txt) lists `GET /fapi/v1/klines` and open-time identity. The field mapping was checked against Binance's official [legacy USD-M REST schema](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/market-data/rest-api/Kline-Candlestick-Data) and [legacy USD-M WebSocket schema](https://developers.binance.com/legacy-docs/derivatives/usds-margined-futures/websocket-market-streams/Kline-Candlestick-Streams), including REST's twelve fields, absence of `timeZone`, WS `i`/`t`/`T`/`E`/`x`, and inclusive `T` example.
- **Verification:** Locked restore, full Release warnings-as-errors build (0 warnings/errors), Exchange.Tests 70/70, Replay.Tests 61/61, scoped C# format, docs fixtures (11/11), and final docs validator passed with phases=19, milestones=43, tasks=152, ready=none (explicit unresolved authorization blocker). The first T005=DONE validation correctly rejected T006's stale dependency-only blocker; after the user authorized a blocker-text-only T006 clarification, whole-graph validation passed. No T006 implementation/status changed.

```powershell
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & "$HOME\.dotnet\dotnet.exe" restore TradingBot.slnx --locked-mode; exit $LASTEXITCODE'
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & "$HOME\.dotnet\dotnet.exe" build TradingBot.slnx -c Release --no-restore --warnaserror; exit $LASTEXITCODE'
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Exchange.Binance.Tests/TradingBot.Exchange.Binance.Tests.csproj -c Release --no-build --no-restore; exit $LASTEXITCODE'
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj -c Release --no-build --no-restore; exit $LASTEXITCODE'
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & "$HOME\.dotnet\dotnet.exe" format src/TradingBot.Exchange.Binance/TradingBot.Exchange.Binance.csproj whitespace --include src/TradingBot.Exchange.Binance/BinanceKlineIngestion.cs src/TradingBot.Exchange.Binance/BinanceRestContracts.cs src/TradingBot.Exchange.Binance/BinancePublicRestClient.cs src/TradingBot.Exchange.Binance/BinanceWebSocketContracts.cs src/TradingBot.Exchange.Binance/BinanceMarketWebSocketClient.cs --verify-no-changes --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$HOME\.dotnet\dotnet.exe" format tests/TradingBot.Exchange.Binance.Tests/TradingBot.Exchange.Binance.Tests.csproj whitespace --include tests/TradingBot.Exchange.Binance.Tests/BinanceKlineIngestionTests.cs tests/TradingBot.Exchange.Binance.Tests/BinanceRestClientTests.cs tests/TradingBot.Exchange.Binance.Tests/BinanceWebSocketClientTests.cs --verify-no-changes --no-restore; exit $LASTEXITCODE'
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; & pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & pwsh -NoProfile -File tools/validate-docs.ps1; exit $LASTEXITCODE'
pwsh -NoProfile -File tools/t005-kline-smoke.ps1
pwsh -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'
```

- **Smoke output:** `Smoke passed: timeframe identities, REST-to-WebSocket same-hash deduplication, revision conflict, inclusive close normalization, and three invalid-input diagnostics.` The smoke script was temporary and removed after the run. No live Binance data was fetched.
- **Canonical source:** `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; unchanged.
- **Changed files:** `src/TradingBot.Exchange.Binance/BinanceKlineIngestion.cs` (new), `BinanceRestContracts.cs`, `BinancePublicRestClient.cs`, `BinanceWebSocketContracts.cs`, `BinanceMarketWebSocketClient.cs`; `tests/TradingBot.Exchange.Binance.Tests/BinanceKlineIngestionTests.cs` (new), `BinanceRestClientTests.cs`, `BinanceWebSocketClientTests.cs`; `docs/architecture/market-data.md`, `docs/architecture/event-model.md`, `docs/TASKS.md` (T005 evidence/status and summary; T006 blocker text only, status unchanged), and `HANDOFF.md`.
- **Limits/scope:** Cross-source dedup requires callers to pass the accepted revision state; this change does not add durable history, and callers that omit it accept a standalone first observation. Core-ready kline intervals are currently limited to 15m and 1h. The REST schema page's live renderer limitation is recorded above; no production exchange behavior was exercised. No historical IDs, T006 implementation/status, P17, canonical source, App/trading runtime, unrelated task statuses, or user changes were altered; the T006 blocker-only clarification was explicitly authorized. No commit/push.
- **Next:** P15-M03-T006 (SR-02.3) remains BLOCKED pending separate implementation authorization; its existing dependencies P15-M03-T005 and P06-M02-T003 are DONE. Do not start T006 under this authorization.


## P15-M03-T004 (SR-02.1) — DONE (2026-11-09)

- **Scope/authorization:** User explicitly authorized only P15-M03-T004. Dependencies P15-M03-T003 and P14-M01-T002 were DONE. No T005 implementation started. T005 remains BLOCKED; its blocker wording now records that SR-02.2 needs separate authorization because both dependencies are DONE.
- **Root cause:** Single-source delivery already delayed a Bar until `max(EventTime, AvailabilityTime, CloseTime)`, but `ReplaySnapshotBuilder` filtered prefixes using only event time, availability, and sequence. Multi-source delivery honored the Bar close for `ReplayEnvelope` Bars, while its prefix filter also omitted close time. A minute-1 Bar with minute-1 availability and minute-3 close therefore appeared in a Trade-at-minute-2 prefix.
- **Deliverable:** Added shared internal `ReplayCausalVisibility` for inclusive event-time, availability-time, Bar-close, and stable-sequence eligibility. Single- and multi-source delivery and prefix construction now use the same eligibility bounds. Multi-source persisted order and all original event, exchange, receive, availability, and sequence fields remain unchanged; equal timestamps still order by saved stable sequence. No strategy formulas, identity, App trading/runtime, fill simulation, or positions changed.
- **RED/GREEN:** The command below failed before the fix with 2/2 tests failing: both `Assert.DoesNotContain` checks found the minute-1 Bar in the Trade-at-minute-2 prefix. After the fix, the same focused filter passed 2/2. Boundary tests prove inclusion at close minute 3 and availability minute 4 and unchanged causal timestamps/sequences; equal-time tests assert prefix sequence boundaries. The T004 regressions also submit exact duplicates and compare single/multi-source outputs and prefixes across input permutations, proving one delivery and deterministic causal views.

```powershell
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj -c Release --filter "FullyQualifiedName~RunnerPrefixWaitsForBarCloseAndAvailabilityBoundaries|FullyQualifiedName~MultiSourcePrefixWaitsForBarCloseAndAvailabilityBoundaries"'
```

- **Final tests/build:** `TradingBot.Replay.Tests` passed 61/61. Locked restore succeeded. Release build with `--warnaserror` succeeded with 0 warnings and 0 errors. Exact combined command:

```powershell
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" restore tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj --locked-mode; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$HOME\.dotnet\dotnet.exe" build tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj -c Release --no-restore --warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$HOME\.dotnet\dotnet.exe" test tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj -c Release --no-build --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$HOME\.dotnet\dotnet.exe" format src/TradingBot.Backtesting/TradingBot.Backtesting.csproj --verify-no-changes --include src/TradingBot.Backtesting/HistoricalReplayRunner.cs src/TradingBot.Backtesting/MultiSourceReplay.cs src/TradingBot.Backtesting/ReplaySnapshot.cs src/TradingBot.Backtesting/ReplayCausalVisibility.cs; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$HOME\.dotnet\dotnet.exe" format tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj --verify-no-changes --include tests/TradingBot.Replay.Tests/HistoricalReplayRunnerTests.cs tests/TradingBot.Replay.Tests/MultiSourceReplayTests.cs; exit $LASTEXITCODE'
```

- **Format:** Both final scoped `dotnet format ... --verify-no-changes` commands in the combined command exited 0. The first verification had identified the new helper's line endings/UTF-8 BOM; `dotnet format` normalized that file before the final passing checks.
- **Actual replay smoke:** Ran a temporary console against both `HistoricalReplayRunner` and `MultiSourceReplayRunner`; both excluded the Bar before minute 3 and included it at the close boundary. The smoke project was removed after the run.

```powershell
pwsh -NoProfile -Command '& "$HOME\.dotnet\dotnet.exe" run --project tools/t004-replay-smoke/ReplaySmoke.csproj -c Release'
```

Output: `Replay smoke passed: single-source and multi-source exclude the Bar before minute 3 and include it at the close boundary.`

- **Docs/source:** `pwsh -NoProfile -File tools/validate-docs.ps1` passed: `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. `pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1` passed all 11 fixture cases. `pwsh -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'` returned `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; canonical source unchanged.
- **Changed files:** `src/TradingBot.Backtesting/ReplayCausalVisibility.cs`, `HistoricalReplayRunner.cs`, `MultiSourceReplay.cs`, `ReplaySnapshot.cs`; `tests/TradingBot.Replay.Tests/HistoricalReplayRunnerTests.cs`, `MultiSourceReplayTests.cs`; `docs/architecture/event-model.md`, `multi-source-replay.md`, `persistence.md`; `docs/TASKS.md` (T004 DONE; T005 remains BLOCKED with separate authorization blocker); `HANDOFF.md`.
- **Limits/scope:** Smoke uses fixed synthetic records only; no external exchange data or production/live behavior was tested. No P17, canonical source, App trading runtime, fill/position simulation, or unrelated task statuses changed.
- **Next:** Separately authorize P15-M03-T005 (SR-02.2). Do not begin it under this authorization.


## P15-M03-T003 (SR-01.3) — DONE (2026-11-09)

- **Scope/authorization:** P15-M03-T002 was DONE before work. User explicitly authorized only P15-M03-T003. P15-M03-T004 and all other tasks were not started. P14-M01-T002, T004's other dependency, is DONE; T004 remains BLOCKED pending separate implementation authorization.
- **Deliverable:** Added `SignalScenarioAnchor`, explicit versioned expiry/rearm/invalidation policies, immutable event/episode/snapshot contracts, and pure `SignalScenarioProjectionEngine` in `src/TradingBot.Core/SignalScenarioContracts.cs` and `src/TradingBot.Core/SignalScenarioProjectionEngine.cs`. The structural anchor precedes confirmation; `SignalId` uses that anchor event ID and excludes event/availability, run/restart, and publication clocks. Missing required policy or non-distinct rearm policy blocks readiness. Same active logical scenario continues without another alert; a rearm requires a new anchor and elapsed configured delay. Expiry is event-driven and applied at `ExpiresAt <= input.EventTime`. Opposite same-family direction cancellation, explicit invalidation, cross-family overlap links, and immutable linked cancel/expire events retain causal and policy-version provenance. No default duration, numeric production threshold, persistence, runtime composition, fills, or positions were added.
- **Design:** [passive scenario lifecycle](docs/architecture/scenario-lifecycle.md); identity relation clarified in [versioned signal contracts](docs/architecture/signal-contracts.md). `SignalScenarioProjectionSnapshot` round-trips and resumes pure projection; T010 persistence remains future work.
- **Tests/build:** `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SignalScenarioProjectionTests'` — PASS, 7/7. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore'` — PASS, 328/328. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/TradingBot.Core/TradingBot.Core.csproj -c Release --no-restore --warnaserror'` — PASS, 0 warnings/errors.
- **Format:** All scoped whitespace verification commands exited 0:
  - `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format src/TradingBot.Core/TradingBot.Core.csproj whitespace --include src/TradingBot.Core/SignalScenarioContracts.cs --verify-no-changes --no-restore'`
  - `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format src/TradingBot.Core/TradingBot.Core.csproj whitespace --include src/TradingBot.Core/SignalScenarioProjectionEngine.cs --verify-no-changes --no-restore'`
  - `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj whitespace --include tests/TradingBot.Core.Tests/SignalScenarioProjectionTests.cs --verify-no-changes --no-restore'`
- **PowerShell smoke:** Created one explicit 5m/2m test policy, projected a prior anchor through one confirmation, and retried the identical event. Exact command:

```powershell
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; Add-Type -Path "src/TradingBot.Core/bin/Release/net10.0/TradingBot.Core.dll"; $expiry = [TradingBot.Core.SignalScenarioExpiryPolicy]::new(1, "smoke-expiry-v1", [TimeSpan]::FromMinutes(5)); $rearm = [TradingBot.Core.SignalScenarioRearmPolicy]::new(1, "smoke-rearm-v1", [TimeSpan]::FromMinutes(2), $true); $invalidation = [TradingBot.Core.SignalScenarioInvalidationPolicy]::new(1, "smoke-invalidation-v1", $true, $true); $policies = [TradingBot.Core.SignalScenarioPolicySet]::new(1, $expiry, $rearm, $invalidation); $engine = [TradingBot.Core.SignalScenarioProjectionEngine]::new($policies); $anchorTime = [TradingBot.Core.UtcTimestamp]::new([DateTimeOffset]::Parse("2026-11-09T00:01:00Z")); $eventTime = [TradingBot.Core.UtcTimestamp]::new([DateTimeOffset]::Parse("2026-11-09T00:02:00Z")); $identity = [TradingBot.Core.SignalIdentity]::Create([TradingBot.Core.InstrumentId]::new("BTCUSDT"), "BINANCE_USDM", "15m", [TradingBot.Core.Direction]::Long, [TradingBot.Core.SetupType]::BoundaryRejection, "profile-v1", "boundary-rejection-v1", "smoke-anchor"); $anchor = [TradingBot.Core.SignalScenarioAnchor]::new(1, $identity, $anchorTime, 1); $input = [TradingBot.Core.SignalScenarioInput]::Confirm("smoke-stream", "smoke-event", $anchor, $eventTime, $eventTime, 2); $created = $engine.Apply($engine.CreateInitialSnapshot(), $input); $duplicate = $engine.Apply($created.NextSnapshot, $input); if ($created.Status -ne [TradingBot.Core.SignalScenarioTransitionStatus]::Applied -or $duplicate.Status -ne [TradingBot.Core.SignalScenarioTransitionStatus]::Duplicate -or $created.NextSnapshot.Episodes.Count -ne 1) { throw "Scenario smoke failed" }; Write-Output ("smoke=PASS;first={0};repeat={1};alerts={2};signalId={3}" -f $created.Status, $duplicate.Status, @($created.NextSnapshot.Events | Where-Object Kind -eq AlertCreated).Count, $created.NextSnapshot.Episodes[0].Identity.SignalId)'
```

- **Smoke result:** `smoke=PASS;first=Applied;repeat=Duplicate;alerts=1;signalId=07a4d9511b5eb490475cd4deca15fd610ec458615e9030e8963b9b633a55068a`.
- **Docs/hash:** `pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1` — PASS, all 11 cases. `pwsh -NoProfile -File tools/validate-docs.ps1` — PASS: `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. `pwsh -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'` → `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; canonical source unchanged. The final validator run followed task-status and blocker updates.
- **Scope:** No runtime/trading, P17, canonical-source, or unrelated task-status changes. P15-M03-T003 is DONE. P15-M03-T004 remains BLOCKED; only its blocker text was updated to reflect that both dependencies are DONE and separate SR-02.1 authorization is still absent.
- **Next:** Separately authorize P15-M03-T004 (SR-02.1). Do not start it under T003 authorization.

## Previous checkpoint: P15-M03-T002 (SR-01.2) — DONE (2026-11-09)


- **Scope/authorization:** P15-M03-T001 is DONE. User explicitly authorized only P15-M03-T002; P15-M03-T003 and all other tasks were not started. T003 remains BLOCKED pending separate SR-01.3 implementation authorization.
- **Deliverable:** Added immutable BCL-only public signal contracts in `src/TradingBot.Core/SignalContracts.cs`: logical `SignalId` excludes mutable evidence/card content; evidence provenance has public 15m/1h inputs, payload hashes, event/availability times, stable sequence, and decision/HTF cuts; evidence and signal records have independent content hashes; a delivery receipt binds to exact `SignalId`/`SignalRecordHash`; an outcome additionally binds `EvidenceRecordHash`. Unknown evidence needs reasons; `ConfirmedOnClosedBar` requires complete evidence and does not mean validation or order authority. Optional probability/calibration remain both null; strict schema rejects unknown fields. No I/O, secrets, persistence, or trading effects.
- **Design:** [versioned signal contracts](docs/architecture/signal-contracts.md); plan: [SR-01.2](docs/EXECUTION_PLAN.md#sr-01-contracts-and-public-only-profile).
- **Tests/build:** `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SignalContractsTests'` — PASS, 6/6. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore'` — PASS, 321/321. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/TradingBot.Core/TradingBot.Core.csproj -c Release --no-restore --warnaserror'` — PASS, 0 warnings/errors. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format src/TradingBot.Core/TradingBot.Core.csproj whitespace --include src/TradingBot.Core/SignalContracts.cs --verify-no-changes --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & "$env:USERPROFILE\.dotnet\dotnet.exe" format tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj whitespace --include tests/TradingBot.Core.Tests/SignalContractsTests.cs --verify-no-changes --no-restore'` — PASS.
- **PowerShell smoke:** `pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'` → `7.6.9`. Command:

```powershell
pwsh -NoProfile -Command '$ErrorActionPreference = "Stop"; Add-Type -Path "src/TradingBot.Core/bin/Release/net10.0/TradingBot.Core.dll"; $open = [TradingBot.Core.UtcTimestamp]::new([DateTimeOffset]::Parse("2026-11-09T00:00:00Z")); $close = [TradingBot.Core.UtcTimestamp]::new([DateTimeOffset]::Parse("2026-11-09T00:15:00Z")); $htfClose = $open; $identity = [TradingBot.Core.SignalIdentity]::Create([TradingBot.Core.InstrumentId]::new("BTCUSDT"), "BINANCE_USDM", "15m", [TradingBot.Core.Direction]::Long, [TradingBot.Core.SetupType]::BoundaryRejection, "profile-v1", "boundary-rejection-v1", "setup-event"); $setupRef = [TradingBot.Core.SignalInputReference]::new("binance-public", "native-kline-v1", "setup-event", ("c" * 64), [TradingBot.Core.SignalInputRole]::Setup, "15m", $close, $close, 20); $contextRef = [TradingBot.Core.SignalInputReference]::new("binance-public", "native-kline-v1", "context-event", ("d" * 64), [TradingBot.Core.SignalInputRole]::HigherTimeframe, "1h", $htfClose, $htfClose, 10); $decisionCut = [TradingBot.Core.CausalEventCursor]::new($close, 20); $htfCut = [TradingBot.Core.CausalEventCursor]::new($htfClose, 10); $provenance = [TradingBot.Core.SignalProvenance]::Create("profile-v1", ("a" * 64), $decisionCut, $htfCut, [TradingBot.Core.SignalInputReference[]]@($setupRef, $contextRef)); $evidence = [TradingBot.Core.SignalEvidenceRecord]::Create($identity, $provenance, [TradingBot.Core.SignalEvidenceState]::Complete, [string[]]@("quality-reviewed")); $interval = [TradingBot.Core.SignalBarInterval]::new($open, $close); $reference = [TradingBot.Core.SignalReferencePrice]::new("binance-public", "native-kline-v1", "setup-event", $close, [TradingBot.Core.Price]::new([decimal]100)); $signal = [TradingBot.Core.SignalRecord]::Create($identity, $evidence, [TradingBot.Core.SignalAssessmentState]::ConfirmedOnClosedBar, $interval, $close, $reference); $json = [TradingBot.Core.CoreJsonSerializer]::Serialize[TradingBot.Core.SignalRecord]($signal); $restored = [TradingBot.Core.CoreJsonSerializer]::Deserialize[TradingBot.Core.SignalRecord]($json); if ($restored.RecordHash -cne $signal.RecordHash -or $restored.Identity.SignalId -cne $identity.SignalId -or $restored.Probability -ne $null) { throw "signal smoke round-trip mismatch" }; $restored.ValidateEvidence($evidence); Write-Output ("smoke=PASS;assessment={0};signalId={1};recordHash={2};probability={3}" -f $restored.AssessmentState, $restored.Identity.SignalId, $restored.RecordHash, $restored.Probability)'
```

- **Smoke result:** `smoke=PASS;assessment=ConfirmedOnClosedBar;signalId=af42f525ee15a1bdab8c357897f5a3e25132e109d174d7ec4ff867d4ddb3256c;recordHash=c46e664585e061f636a1102e9d32dea55a261f4c7c4861592750b1cdbcab333e;probability=`.
- **Docs/hash:** `pwsh -NoProfile -File tools/validate-docs.ps1` — PASS: `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. `pwsh -NoProfile -Command '(Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256).Hash'` → `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; canonical source unchanged. The final validator run occurred after task-status and blocker updates.
- **Scope:** No runtime/trading/P17/canonical-source changes. P15-M03-T002 is DONE; P15-M03-T003 remains BLOCKED with its explicit separate-authorization blocker. No other task statuses changed.
- **Next:** Separately authorize P15-M03-T003 (SR-01.3). No T003 implementation started.


## Previous checkpoint: P15-M03-T001 (SR-01.1) — DONE (2026-11-09)

- **Scope/authorization:** P01-M03-T002 and P07-M02-T002 were DONE. User explicitly authorized only P15-M03-T001. It is DONE; P15-M03-T002 remains BLOCKED and was not started.
- **Deliverable:** Added a BCL-only, immutable, format-1 Core configuration for public native Binance USD-M 15m setup and 1h context. Source, profile/formula versions, history/seed/warmup/freshness, indicator periods/methods/units, and point-in-time universe bounds are explicit. Unset strategy values remain null and produce stable `UnknownConfiguration` reasons; no parameter values or defaults were invented. `CoreJsonSerializer` rejects unsupported timeframes/units and unknown fields, including private/activation/quantity/leverage/portfolio fields, without reading secrets.
- **Design:** [public profile contract](docs/architecture/public-signal-profile.md); SR-01.1 plan record in [EXECUTION_PLAN](docs/EXECUTION_PLAN.md#sr-01-contracts-and-public-only-profile). Format version `1` is a schema version only. No complete profile is declared; configuration readiness is not runtime, statistical, or trading readiness.
- **Tests/build:** `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore'` — PASS, 315/315. `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/TradingBot.Core/TradingBot.Core.csproj -c Release --no-restore --warnaserror'` — PASS, 0 warnings/errors. Scoped `dotnet format ... whitespace --verify-no-changes --no-restore` passed for the new Core and Core.Tests files; the exact commands are recorded below.
- **Smoke:** `pwsh -NoProfile -Command 'Add-Type -Path "src/TradingBot.Core/bin/Release/net10.0/TradingBot.Core.dll"; $p = [TradingBot.Core.PublicSignalProfileConfiguration]::Missing; Write-Output ("state={0};ready={1};profileUnknown={2}" -f $p.State.ToString(), $p.IsReady, (@($p.UnknownReasons) -contains "profile-version-unknown"))'` → `state=UnknownConfiguration;ready=False;profileUnknown=True`.
- **Docs/hash:** `pwsh -NoProfile -File tools/validate-docs.ps1` — PASS: `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. `Get-FileHash docs/sources/FINAL_TECH_SPEC.md -Algorithm SHA256` → `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`; canonical source unchanged.
- **Graph correction:** After P15-M03-T001 became DONE, the validator correctly rejected P15-M03-T002 as BLOCKED only by its completed dependency. Kept P15-M03-T002 status BLOCKED and changed only its blocker text to require separate SR-01.2 authorization; no SR-01.2 work or other task-status changes. Re-running the validator passed.
- **Next at that checkpoint:** Separately authorize P15-M03-T002 (SR-01.2); this was fulfilled by the subsequent user request recorded above. No trading, runtime integration, P17 changes, or canonical-source edits.

### Exact scoped format checks

- `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format src/TradingBot.Core/TradingBot.Core.csproj whitespace --include src/TradingBot.Core/PublicSignalProfileContracts.cs --verify-no-changes --no-restore'` — PASS.
- `pwsh -NoProfile -Command '& "$env:USERPROFILE\.dotnet\dotnet.exe" format tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj whitespace --include tests/TradingBot.Core.Tests/PublicSignalProfileConfigurationTests.cs --verify-no-changes --no-restore'` — PASS.

## Previous checkpoint: GOV-01.1 / GOV-01.2 — docs gate and formal registration (2026-11-09)

**На предыдущем GOV checkpoint результат: GOV-01 закрыта; формальная регистрация завершена, implementation/activation approval ещё не выдана.** Для GOV-задач изменены fixture harness и planning docs: `tools/tests/validate-docs.Tests.ps1`, `docs/PROJECT_SPEC.md`, `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, `docs/research/SIGNAL_ONLY_IMPLEMENTATION_ROADMAP.md`, `HANDOFF.md`. Runtime, canonical source, торговое поведение, прежние task rows/evidence/statuses и P17 blockers не менялись.


- Startup selector: `ready=none (explicit unresolved blocker)`. GOV-01.1 and GOV-01.2 are formal DONE tasks; the registered signal branch has no READY task because SR-01.1 remains explicitly BLOCKED pending separate implementation authorization.
- Acquired official `Microsoft.PowerShell` 7.6.9.0 with `winget`; package hash verification succeeded (`da75c8aa23edda52e888fb586a46b923ab37a7c243719c2e8914b00130c5917a`). `pwsh --version` and `$PSVersionTable.PSVersion` both report `7.6.9`.
- Native fixture command `pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1`: PASS, all 11 cases; final output: `All validator fixture cases passed.`
- Native validator command `pwsh -NoProfile -File tools/validate-docs.ps1`: exit 0, `docs validation passed: phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. Canonical source hash passed.
- At the GOV checkpoint, GOV-01.1 and GOV-01.2 passed on the registered graph. Mapping audit: 33 unique work IDs → 33 unique formal IDs. The 119 pre-existing rows remained alongside two DONE GOV tasks and 31 BLOCKED SR tasks; no Phase 19 or implementation READY was added. The next step then was separate implementation authorization for P15-M03-T001 (SR-01.1). No SR implementation or trading started under GOV authorization.


## Detailed signal-only planning follow-up at the prior GOV checkpoint


### Where to read the plan

- [EXECUTION_PLAN: brainstorming, quality gates и 33 полные карточки](docs/EXECUTION_PLAN.md#signal-only-detailed-work-plan): у каждой задачи есть результат, acceptance, конкретные проверки/smoke и ссылки на A/B/P, repo architecture/code и применимые primary API sources.
- [TASKS: formal backlog and one-to-one mapping](docs/TASKS.md#signal-only-work-item-mapping): 33 work IDs map exactly once; formal rows are the only status/dependency/evidence source.
- [A: research plan, §§1–7](additional-strategy-research-plan.md#L5-L122) фиксирует public 15m/closed 1h baseline; [B: S1–S6/TF/timing/future architecture](%D0%BC%D0%BE%D0%B8%20%D0%BF%D1%80%D0%B5%D0%B4%D0%BB%D0%BE%D0%B6%D0%B5%D0%BD%D0%B8%D1%8F..txt#L13-L210) — hypotheses; [P: Prizrak ZIP](Prizrak_v2.1.zip) и [Pine audit](docs/research/PRIZRAK_V21_ALGORITHM_AUDIT.md) — изучаемый алгоритм, не инструкция торговли. Литература A §9 используется как ранее проверенный source register, не новое заявление о полном чтении книг.

### Selected work sequence

Выбран разрез по проверяемому результату/границе отказа, а не восемь мегазадач или module-first framework. Девять групп, включая governance; SR-08 стоит перед optional SR-07 в каталоге. Зависимости отдельных work items указаны в TASKS; одно завершённое отрицательное исследование не требует прибыльного результата для перехода к следующей гипотезе.

| Группа / work IDs | Осязаемый выход / подробности |
|---|---|
| GOV-01.1–01.2 | [Behavioral docs fixtures, native PowerShell 7, formal registration](docs/EXECUTION_PLAN.md#gov-01-tooling-and-registration) — completed and verified. |
| SR-01.1–01.3 | [Public profile, signal schema, scenario/expiry/rearm](docs/EXECUTION_PLAN.md#sr-01-contracts-and-public-only-profile); unknown не Ready. |
| SR-02.1–02.5 | [Replay close-bound fix, TF identity, feed, intake/recovery, real coverage](docs/EXECUTION_PLAN.md#sr-02-causal-public-data-and-coverage). |
| SR-03.1–03.3 | [Reuse existing setups, atomic signal store, отдельный CLI/root](docs/EXECUTION_PLAN.md#sr-03-existing-setups-and-durable-signals), без risk/order path. |
| SR-04.1–04.3 | [JSON/console, durable Telegram outbox, actual delivery proof](docs/EXECUTION_PLAN.md#sr-04-local-output-and-telegram-delivery). |
| SR-05.1–05.3 | [Frozen signal-study manifest, independent outcomes, opportunity report](docs/EXECUTION_PLAN.md#sr-05-frozen-signal-evaluation-and-outcomes), независимо от SR-04 после SR-03. |
| SR-06.1–06.2 | [Declared bounded run и actual prospective/restart/replay evidence](docs/EXECUTION_PLAN.md#sr-06-bounded-prospective-runtime), после SR-04/SR-05. |
| SR-08.1–08.2 | [Baseline E2E/checks и operator runbook/handoff](docs/EXECUTION_PLAN.md#sr-08-final-baseline-acceptance), без зависимости от SR-07. |
| SR-07.1–07.10 | [Optional controls/R1/R2/three Flow waves/timing/TF/OB-POC/B-C/S6/ML studies](docs/EXECUTION_PLAN.md#sr-07-optional-registered-research), каждая с собственным manifest/H0/reject criteria. |

### Governance and next action

- At the prior GOV checkpoint, the graph had 19 phases, 43 milestones, and 152 tasks; 110 DONE, 42 BLOCKED, 0 READY. The original 119 task IDs/statuses/evidence remained alongside two DONE GOV tasks and 31 BLOCKED SR tasks. The selector result was `ready=none (explicit unresolved blocker)`; P17 was not selected or altered.
- **At that checkpoint:** startup selected no task. P15-M03-T001 was BLOCKED pending separate implementation authorization; it is now DONE as recorded above. Unknown profile values remain unknown; no parameters or thresholds were inferred, and no SR implementation or trading began under GOV authorization.
- The formal task rows are the sole authoritative status/dependency/evidence source; the planning index only maps work IDs to formal IDs. SR-08 rows P15-M03-T020/T021 precede optional SR-07 rows P15-M03-T022–T031; SR-07 is not a baseline dependency.
- Остающиеся prerequisite decisions: полный required signal profile до Ready/public run; test destination/provisioning до external Telegram proof; PIT dataset/labels/estimand/precision/cost-latency до statistical study; bounded duration/resources/health до SR-06. Ничего из этого не заполнять оптимальными числами без evidence. Секреты сейчас не запрашиваются.
- Не нужны для baseline: private credentials/reconciliation, risk/portfolio authorization, paper/demo/testnet orders, 30m/1h setup studies, Flow/OB/POC/Lane B/C/ML. Не делать shadow gate cutover, clone S1/S4 evaluator или automatic V1 refactor. Canonical 5m source сохраняется; approved 15m amendment имеет оговорённый scope.
- Earlier missing-PowerShell/stale-fixture observations are historical and superseded by the native PowerShell 7 checks above. No .NET, signal-runtime, Telegram-send, or trading study was run for GOV-01.2; runtime and trading behavior remain unchanged.

### Verification of registered planning graph

- **PASS:** `pwsh -NoProfile -File tools/tests/validate-docs.Tests.ps1` exited 0 and passed all 11 cases: baseline, CRLF `.gitignore`, duplicate IDs, unknown requirement reference, uncovered requirement, dependency cycle, invalid status, multiple READY, explainable zero READY, unexplained zero READY, next-task transition.
- **PASS:** `pwsh -NoProfile -File tools/validate-docs.ps1` exited 0 with `phases=19; milestones=43; tasks=152; ready=none (explicit unresolved blocker)`. This validates the existing 00–18 phase graph, task references, status/dependency/cycle rules, and requirement coverage.
- **PASS:** 33 unique work IDs map to 33 unique formal IDs; 152 formal rows comprise 110 DONE and 42 BLOCKED. The original 119 task IDs/statuses/evidence and Phase 17 blockers remain unchanged. SR-08 precedes SR-07; no optional SR-07 dependency enters baseline SR-08.
- **PASS:** canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 is `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`, matching the validator's immutable-source check. Runtime/trading were not changed.

## Architecture/research audit — 2026-11-09

- Scope: read-only code/research audit and planning documents only. Preserve the
  approved 15m primary / initial 1h public signal profile, canonical source,
  all existing task rows/statuses, P17 hold, runtime preparation, and `graft/`.
  No trading, private transport, Telegram send, deployment, commit, or push.
- Deliverables: [architecture review](docs/research/TRENDBOT_FINAL_ARCHITECTURE_REVIEW.md),
  [Pine audit](docs/research/PRIZRAK_V21_ALGORITHM_AUDIT.md),
  [research decisions](docs/research/STRATEGY_RESEARCH_DECISIONS.md), and
  [signal-only roadmap](docs/research/SIGNAL_ONLY_IMPLEMENTATION_ROADMAP.md).
  All three available materials were read completely; the research plan and ZIP
  have unsuffixed local names, so identity to the requested numbered filenames
  is not assumed. Source hashes and read-only Pine extraction are recorded there.
- Git preflight: local HEAD, origin/main, and public main matched
  `76ea2bac6a8f1baf25783fe9a0923fd47af03179`; dirty planning edits and untracked
  C# preparation predated this audit. The current graph has 108 DONE, 11 BLOCKED,
  and no READY tasks. No existing task transition was made.
- Verification: installed SDK 10.0.401 matches global.json; locked restore and
  full Release warnings-as-errors build passed (zero warnings/errors); all five
  existing test assemblies passed, 710/710 with no skips. Actual default App
  and DataTool invocations printed their scaffold status.
- Separate throwaway public/local smoke reproduced pre-close Bar leakage in
  both replay prefixes and colliding Core IDs for 15m/1h bars with the same
  opening time. Unknown passive configuration stayed UnknownConfiguration;
  SQLite event-journal reopen/retry preserved one identical envelope. Native
  public REST returned three closed grid-valid bars for each TF; this is not
  prospective runtime, Telegram delivery, a frozen dataset, or edge evidence.
- Pre-existing failures remain: full format verification reports LF/charset in
  untracked LaneASignalEvaluator.cs. `pwsh` is unavailable; the unchanged docs
  validator body passed through an explicitly labelled UTF-8 Windows PowerShell
  5.1 invocation, while compatibility fixture execution passed seven cases and
  then failed on the stale P17-M01-T002 evidence marker. Native PowerShell 7
  fixture validation is not claimed. Repository tooling scripts were not edited.
- Registration: the roadmap supplies a complete, non-applied requirement/task
  diff and independent signal branch. The fixture harness also hardcodes the
  old task/milestone counts. Repair it in a separately authorized tooling task;
  do not rewrite P17 blockers to satisfy wording tests, weaken trade gates, or
  start SR implementation in this audit. 30m/1h setup TF, three Flow waves,
  R1/R2, OB/POC, Lane B/C, routing and ML remain separate candidate studies.
- Final audit evidence: current standalone validator passed again via the labelled
  PS5 invocation; the final compatibility fixture run still stopped after seven
  PASS cases on the pre-existing marker. Both native `pwsh` commands are unavailable.
  `git diff --check` found no whitespace errors (only LF-to-CRLF warnings);
  separate new-file checks found no report whitespace errors. Local report links
  and table column counts passed. All 119 historical task rows are identical;
  all 285 captured runtime/tooling hashes, canonical SHA-256, original attachments,
  and the pre-existing execution-model edits remain unchanged.
- The non-applied registration diff passed `git apply --check`; the unchanged
  standalone validator body on a temporary proposal returned 19 phases,
  43 milestones, 128 tasks, and no READY. This is not a fixture-suite success or
  an applied task transition. Audit-owned temporary smoke/graph-fixture trees
  were removed; the read-only Pine extraction and original attachments remain.
- Next: authorize documentation-validator fixture maintenance, obtain a usable
  PowerShell 7 invocation, then revalidate and register the independent signal
  branch through the normal startup protocol. Do not resume P17.

## Current user direction — 2026-11-06

- The current request revises planning: **15-minute primary signals, without
  opening orders**. It does not authorize continuation or activation of Phase 17,
  nor code changes or a service run in this pass. The Phase 17 checkpoint below
  remains historical/current blocker evidence, not the next action for this request.
- Updated [additional-strategy-research-plan.md](additional-strategy-research-plan.md):
  public closed 15-minute bars, initial 1-hour context, SETUP-01/02, durable
  explainable signals, JSON/console and output-only Telegram, separate observed
  outcomes, and independent software-quality/OOS conclusions. Optional Lane B/C,
  MA baseline, and new setup families cannot delay the baseline signal product.
- Signal-only implementation must have its own registered task branch before
  code changes. Proposed SR-01–SR-08 do not change existing task IDs/statuses or
  waive gates. Private reconciliation, trading limits, local paper, exchange-test,
  and limited-live approval are not signal-only prerequisites. No private executor,
  order intent, reservation, position, production/test/demo order is in scope.
- Literature was reread by the specific sections recorded in the plan, not in
  full. The Ultimate MA ZIP contains an MT4 `.ex4`, generic installation material,
  no source formula, and a link whose search record identifies an MT5 product.
  Binary identity, formula, licensing for code reuse, and repaint behaviour remain
  unverified. It was not executed and is excluded from the critical path.
- Approved scope is mirrored in PROJECT_SPEC/EXECUTION_PLAN/TASKS. Canonical source,
  existing C# preparation, task statuses, and unrelated edits are preserved.
- Verification: the unchanged docs validator passed with `phases=19`,
  `milestones=41`, `tasks=119`, and `ready=none (explicit unresolved blocker)`;
  its built-in canonical-source SHA-256 check passed. All 19 unique local
  Markdown targets in the revised plan exist, and scope notices in the four
  companion documents agree. `pwsh` was not on PATH; direct Windows PowerShell
  execution failed on UTF-8 decoding, then the unchanged validator body passed
  via stdin with explicit UTF-8 input and Get-Content encoding. No .NET checks,
  signal runtime, Telegram delivery, or trading-quality evidence is claimed.

## Retained execution checkpoint

- Phase: 17 — Limited Live
- Milestone: P17-M01 — Constrained live gate
- Current task: P17-M01-T001 (BLOCKED after configuration implementation; approval/evidence missing)
- Authorization: the user authorized development and checks for P17-M01-T001 → T002 → T003 in dependency order. This does not waive any gate, approve parameters, or authorize P17-M02.
- Next task: P17-M01-T001 remains blocked pending explicit approval of the exact parameters, verified preceding-gate evidence, and a canonical accepted `PromotionReviewRecord` bound to that evidence.

## P17-M01 checkpoint review — 2026-11-05

- Rechecked the repository for new seed/limit approvals, frozen OOS and
  reconciliation evidence, operational shadow, separate local-paper evidence,
  externally verified Binance exchange-test evidence, and canonical approval
  artifacts. None was found. This repository review cannot establish whether
  evidence exists outside the workspace.
- T001 remains BLOCKED. Resume it only when the real evidence from each required
  lane is available and a canonical accepted `PromotionReviewRecord` plus
  `LimitedLiveParameterApproval` binds that evidence to the exact seed/limits,
  configuration identity, and review.
- T002 remains BLOCKED until T001 is DONE; complete and verify guardrails and
  rollback after that transition. T003 remains BLOCKED until T002 is DONE;
  collect operational gate evidence after that transition. Do not start P17-M02.
- Existing uncommitted P17 code and architecture edits are preserved. They are
  preparation only and do not count as approval or gate evidence. No parameters
  were invented; no orders or activation were run.

## P17-M01-T001 configuration evidence

- The current authorization covers T001 → T002 → T003 in dependency order. It
  does not waive acceptance gates, approve any parameter, or authorize P17-M02;
  the older T001-only wording below records the earlier implementation pass.

- Startup: `main` was clean; SDK `10.0.401` matched `global.json`; locked restore
  and the docs validator passed; P16-M02-T003 was DONE. The user explicitly
  authorized T001 only. T001 moved BLOCKED → READY → IN_PROGRESS before work.
- Evidence review: the canonical source contains historical candidate seeds,
  not an approval record. Existing local shadow/paper/exchange-test fixtures are
  synthetic; external Binance test-environment evidence and an explicit
  limited-live parameter approval are absent. No numeric seed or risk limit was
  added.
- TDD RED: the initial focused runtime profile discovered two tests and both
  failed at runtime with `NotImplementedException` from the new configuration
  shell. The approval-binding correction then discovered four tests with one
  runtime `NotImplementedException` for the missing parameter-approval record.
  GREEN: `LimitedLiveConfigurationTests` passed 4/4, covering nullable unknown
  parameters, blocker reporting, strict JSON rejection, canonical approval
  round-trip, and exact parameter/review/configuration binding.
- Acceptance: `PromotionReviewRecordTests` 10/10,
  `ExchangeTestConfigurationTests` 17/17, and
  `ActivationSeparationEvidenceTests` 4/4 passed. App Release build passed with
  warnings-as-errors; scoped format passed after repository BOM/CRLF formatting.
- Outcome: the inert App contract retains caller-supplied decimal parameters,
  evidence IDs, and strict versioned JSON with explicit-path loading. Caller
  supplied approval strings remain unverified blockers. A resolved approval can
  only be bound through a canonical accepted `PromotionReviewRecord` with
  provided non-synthetic evidence and no blockers/unknowns; parsing revalidates
  the record and exact evidence binding. The contract has no credentials,
  network, order, or activation surface. T001 remains BLOCKED because no
  approved parameter set or verified external live-transition evidence exists.
  At the time of this T001 pass, T002 was not started. Canonical
  source and ignored `graft/` remain unchanged; no real order or production
  credential was used.

Current blocker review: T001 remains BLOCKED because the exact seed/limits have
no explicit approval, preceding-gate evidence is not externally verified, and
no canonical accepted `PromotionReviewRecord` binds those exact parameters to
provided non-synthetic evidence. T002/T003 remain blocked by dependency;
P17-M02 is outside the current authorization.

To resume T001, provide all of the following: a real frozen OOS result with its
reconciliation checkpoint; operational shadow evidence; separate local-paper
evidence; externally verified Binance exchange-test evidence; and an explicit
parameter-approval record whose canonical content binds the exact limited-live
parameters to the accepted promotion review. Synthetic local fixtures do not
satisfy these inputs.

## P17-M01-T002 inactive guardrail preparation

- The user separately authorized inactive T002 preparation while T001 remains
  BLOCKED. T002 remains BLOCKED and cannot become DONE until T001 acceptance,
  approved parameters, and verified external evidence are present.
- TDD RED: the focused `LimitedLiveGuardrailsTests` profile discovered six tests
  and all six reached the runtime `NotImplementedException` shell. GREEN:
  `LimitedLiveGuardrailsTests` passed 7/7 after the evaluator was implemented.
- `LimitedLiveGuardrails.Evaluate` is a pure App evaluator. It blocks entry when
  configuration is not accepted, kill-switch state is unknown/enabled,
  reconciliation is not acknowledged, a position is unknown/manual, partial
  fill protection is unknown/missing, restart recovery is unverified, or rollback
  is unavailable. Unsafe states require rollback; the evaluator exposes no submit,
  dispatch, network, credential, or activation path and adds no numeric limits.
- This is implementation evidence only. No live order, production credential,
  network call, T002 gate approval, T003 evidence, or P17-M02 work occurred.

- The T001 correction adds `LimitedLiveParameterApproval`. A promotion review
  alone cannot authorize a caller-supplied parameter map: the explicit approval
  record stores the exact parameter content, configuration identity, review
  identity, approver reference, and timestamp in canonical JSON. Changed values
  fail the binding check. No external approval is present, so T001 remains
  BLOCKED.

## CI formatting follow-up

- CI run `41337135367` failed at Verify formatting because the repository index
  stores C# as LF while `.editorconfig` requires UTF-8 BOM/CRLF. `.gitattributes`
  now pins `*.cs` to CRLF across checkout platforms, and the four tracked C# files
  missing the BOM were normalized without logic changes.
- Local locked restore, full `dotnet format TradingBot.slnx --verify-no-changes`,
  staged/unstaged diff checks, and the documentation validator pass. Remote CI
  has not been rerun, so it remains unverified for this change.
- The standalone validator intentionally reports `ready=none (explicit
  unresolved blocker)` while P17 is held. The fixture harness now builds its
  READY/transition cases from the current P17 tail graph and all nine cases
  pass, including multiple READY and explainable zero READY coverage.

## CI portability follow-up

- CI run 41338371376 passed format/build on both operating systems, then
  exposed three platform assumptions: Windows fixture .gitignore line endings
  hid the graft/ ignore, the project-graph test treated Windows separators as
  POSIX filenames, and Linux did not recognize a Windows drive-qualified source
  path as absolute.
- The validator now recognizes the exact `graft/` ignore line with either LF or
  CRLF line endings; the fixture preserves copied `.gitignore` bytes and adds
  an explicit CRLF case. Project-reference inspection normalizes separator
  syntax, and manifest source validation rejects Windows drive and UNC absolute
  paths on every host. A direct cross-platform Windows-path guard test was
  added.
- Local Core fixture tests passed 10/10, Infrastructure manifest tests 8/8;
  full Core.Tests passed 302/302 and Infrastructure.Tests 115/115. All nine
  documentation fixtures passed, the standalone validator passed, locked
  restore, scoped format, and staged/unstaged diff checks passed.
  Remote CI has not been rerun for this follow-up.

## Validator CRLF CI follow-up

- CI run `41339564745` passed Ubuntu and Windows format/build/tests and the
  documentation fixtures, then Windows failed the final validator because its
  CRLF `.gitignore` did not match the previous LF-only `graft/` expression.
- The validator now matches only the exact `graft/` line with an optional CRLF
  terminator. The TDD fixture first reproduced the Windows failure, then
  passed both LF and CRLF `.gitignore` cases after the fix; no product,
  canonical-source, or `graft/` change was made.
- Remote CI has not been rerun for this follow-up.

## Canonical source CI follow-up

- CI run 41339020087 reached Ubuntu tests successfully, then the docs fixture
  reported the canonical source index/worktree line-ending mismatch: the LF
  index content hashed FCBC50C58ACBBF94C314D76C23DE7456D1085BFDAC8BA635AF9DFBB473A87245
  while the immutable working source hashes to
  77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54.
- The repository attributes now mark only docs/sources/FINAL_TECH_SPEC.md as
  -text. The staged canonical bytes are 66089 bytes with the required
  77E87... SHA-256; normalized content matches the prior LF blob byte-for-byte.
  A fresh index materialization reproduced the required SHA and passed the
  standalone documentation validator after the temporary Windows .gitignore
  line endings were normalized. No semantic source edit was made.
- Remote CI has not been rerun for this follow-up.

## P16-M02-T003 evidence

- Startup: the required project documents, Git status, SDK, dependencies, and
  documentation validator were reread. T002 was DONE and the validator selected
  T003 READY. The canonical source hash and ignored `graft/` were preserved.
- TDD RED: the initial evidence fixture compile caught strict JSON/test-fixture
  construction errors. After compilation was corrected, the behavioral runtime
  RED discovered 6 `ExchangeTestEvidenceTests` and failed 1 because
  credential-bearing provenance metadata was accepted.
- GREEN: `ExchangeTestEvidence` now reads one serializable persisted snapshot,
  retains lifecycle/fill/reservation/checkpoint/restart/reconciliation/
  idempotency and causal identities, rejects credential-bearing provenance and
  mismatched event run identities, and accepts only the Binance USD-M test
  environment. Format-v1 canonical JSON is strict and SHA-256 content-addressed;
  deserialization rejects tampering, unknown members, and wrong environments.
  Export is UTF-8, flushed, atomic, no-overwrite, and idempotent for identical
  bytes. Raw frames, credentials, signatures, listen keys, and credential-
  bearing payloads have no evidence representation. Promotion is only the
  `ExchangeTest` category and always carries execution-authority and real-money
  activation blockers; local-paper evidence is separate.
- Profiles: `ExchangeTestEvidenceTests` 6/6, atomic persistence 2/2, runtime
  9/9, executor 4/4, Binance adapter 61/61, Infrastructure 114/114, and full
  IntegrationTests 162/162. Locked restore, affected Release builds with
  `--warnaserror`, scoped format, docs validation, `git diff --check`, canonical
  hash, graft-ignore, and count-only secret scan passed.
- Limits: all profiles use synthetic local fixtures/persistence. External
  Binance connectivity and verified test-environment evidence were not run; no
  credentials were read, no real order/live activation occurred, and no
  remote-CI, commit, or push claim is made. P17 remains untouched.

## P16-M02-T002 continuation GREEN

- Startup: the required project documents, Git status, SDK, official Binance
  REST/private-stream documentation, and dependency validator were rechecked.
  T001 was DONE; this continuation reopened T002 and kept T003 blocked until
  the focused runtime profile was green. The validator now selects
  `P16-M02-T003` READY.
- TDD RED: after the requested runtime behavior was added, the affected Release
  build first exposed 11 missing helper errors. The discovered focused
  `ExchangeTestRuntimeTests` profile then failed 5/9: the private stream was
  subscribed after order submission, partial entry fills had no actual
  reduce-only protection, protection failure was treated as safe, and
  reconciliation discarded manual/position identities.
- GREEN: `BinanceExchangeTestTransport` connects the private test stream before
  the order side effect; it maps durable client IDs, requires fresh matching
  `ORDER_TRADE_UPDATE`, places and REST-confirms a reduce-only `STOP_MARKET`
  Algo order for an actual partial fill, retains the normalized protective
  receipt and position snapshot, and fails closed on Algo/stream uncertainty.
  Reconciliation checks one-way mode, current positions, bounded order history,
  private trade IDs, and order/position identity parity; missing history never
  authorizes a retry. The runtime handler rejects non-test REST origins and
  redirects. `ALGO_UPDATE` remains opaque and explicitly blocks until dedicated
  Algo REST reconciliation is available.
- GREEN profiles: `ExchangeTestRuntimeTests` 9/9,
  `ExchangeTestExecutorTests` 4/4, Binance adapter 61/61, Infrastructure
  114/114, and full IntegrationTests 156/156. Affected Release builds with
  `--warnaserror`, locked restore, scoped format, docs validation, and
  `git diff --check` passed. Canonical source SHA and ignored `graft/` were
  unchanged. No credentials, network orders, live activation, commit, push, or
  remote-CI claim was made; external Binance connectivity remains unverified.
- Scope: T003 is now READY. Do not start P17, merge this evidence with
  local-paper evidence, or treat the local profiles as real-money authorization.

## P14-M01-T001 evidence

- Startup: the required documents were reread, Git was clean, and the docs
  validator selected `P14-M01-T001`; both dependencies were DONE.
- TDD RED: the focused Release Infrastructure.Tests DLL discovered one
  `MultiSourceDatasetManifestTests` case and failed at runtime because the Core
  manifest contract was absent.
- GREEN: Core now defines format-one source, causal ordering, and dataset
  manifest contracts with explicit lane/coverage/quality, source-local and
  dataset-wide sequence identities, unknown-state retention, reference/bounds
  validation, and canonical SHA-256 identity. Infrastructure atomically
  persists and reopens manifests and rejects changed content.
- Acceptance: `MultiSourceDatasetManifestTests` 6/6,
  `CaptureDatasetManifestTests` 7/7, `LaneCCaptureExclusionTests` 6/6,
  `CoreBoundaryTests` 2/2, and `ArchitectureTests` 1/1 passed. Core.Tests and
  Infrastructure.Tests Release warnings-as-errors builds and scoped format
  verification passed. `tools/validate-docs.ps1` and `git diff --check` passed.
- Scope: no packages, schema migration, execution behavior, canonical source,
  `graft/`, commit, or push. `P14-M01-T002` is READY and must repeat preflight
  before implementation.

## P14-M01-T002 evidence

- Startup: the required documents, Git status, dependencies, and documentation
  validator were rechecked; `P14-M01-T002` was READY after T001.
- TDD RED: the focused Release Replay.Tests DLL discovered one
  `MultiSourceReplayTests` case and failed at runtime because the shared runner
  was absent.
- GREEN: Backtesting adds typed records tied to manifest causal references and
  a shared replay clock across Core market events, capture rows/diagnostics,
  and independent Lane B liquidation observations. Delivery uses availability,
  event time, persisted global sequence, and bar close. Each callback receives
  one cursor and an immutable causal prefix. Missing rows and conflicting
  payloads fail before delivery; exact duplicates are recorded and delivered
  once. Existing single-stream overloads remain unchanged.
- Acceptance: `MultiSourceReplayTests` 6/6,
  `HistoricalReplayRunnerTests` 4/4, `LaneBReplayTests` 4/4,
  `BacktestRegressionTests` 5/5, and the affected Replay.Tests Release build
  with `--warnaserror` passed. T001 manifest profile was rerun 6/6 after the
  shared evidence-reference field was added. Scoped format verification,
  documentation validation, and `git diff --check` passed.
- Scope: no package, persistence schema change, live transport, order,
  canonical source, `graft/`, commit, or push. `P14-M01-T003` is DONE after its
  acceptance passed; see the evidence below.

## P14-M01-T003 evidence

- Startup: all five required documents, Git status, task dependencies, SDK, and
  `tools/validate-docs.ps1` were checked before this task; T002 was DONE and
  T003 was selected READY. The canonical source and `graft/` remain untouched.
- TDD RED: the focused Release IntegrationTests DLL built and discovered one
  `MultiLaneDecisionTraceRegressionTests` case, then failed at runtime because
  `MultiSourceReplayResult.ReadinessReasons` was absent.
- GREEN: Backtesting results now expose deterministic readiness reasons for
  incomplete source coverage and non-ready source quality. Replay remains
  available for research, while `DecisionReadinessConfirmed` requires successful
  replay and complete/ready metadata for all included sources.
- Regression fixture: IntegrationTests persists and reopens four capture rows
  and the multi-source manifest, then replays capture and funding lanes and runs
  Lane C features/shadow, Lane B evaluation, Lane A setup evidence, lifecycle
  transitions, Decision, risk evaluation, research plan, and fill simulation.
  Identical Run A/Run B canonical traces match; changing funding rate changes
  Lane B identity and Decision outcome. Partial/degraded capture coverage keeps
  its reasons and blocks the Lane C decision evidence even though the synthetic
  feature values are individually ready. Numeric fixture inputs are test-only.
- GREEN and acceptance: `MultiLaneDecisionTraceRegressionTests` passed 3/3;
  `MultiSourceDatasetManifestTests` 6/6, `MultiSourceReplayTests` 6/6,
  `LaneCEvidenceReplayTests` 2/2, and `ArchitectureTests` 1/1 passed. Sequential
  Core, Infrastructure, Backtesting, App, Infrastructure.Tests, Replay.Tests,
  and IntegrationTests Release builds with `--warnaserror` passed with zero
  warnings/errors. Scoped format verification, `tools/validate-docs.ps1`, and
  `git diff --check` passed. Validator selects `P14-M02-T001` READY.
- No package, schema migration, project-graph change, production activation,
  commit, or push was made. Implementation stops before `P14-M02-T001`.

## P14-M02-T001 evidence

- Startup: reread `AGENTS.md`, `docs/PROJECT_SPEC.md`,
  `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and this handoff; Git was clean,
  SDK `10.0.401` matched `global.json`, and the docs validator selected only
  `P14-M02-T001` READY with `P14-M01-T003` DONE.
- TDD RED: the Release Replay.Tests build completed with zero warnings/errors;
  direct execution of `TradingBot.Replay.Tests.FrozenOosEvaluationTests`
  discovered 6 tests and failed all 6 at runtime with `NotImplementedException`
  from the compile-only evaluation definition shell. RED did not use compile
  failure or zero discovery.
- GREEN: Backtesting adds immutable model-version and criteria-evidence
  contracts, a pre-replay definition that retains the old frozen manifest and
  criteria IDs while hashing typed multi-source payloads with all causal
  ordering fields, and a deterministic report/evaluator. Changed research,
  criteria, multi-source manifest, payload, configuration, or model identity
  invalidates the prior record before trace projection. Unknown model versions,
  missing quality references, degraded/unknown coverage, unresolved criteria,
  and absent/unacknowledged reconciliation stay explicit; each delivered trace
  row retains event, exchange, receive, availability, and source-sequence data.
  Reports retain frozen exclusions and exact-duplicate replay diagnostics.
  `Evaluated` records an assessment only, not promotion authority.
- Acceptance: focused `FrozenOosEvaluationTests` passed 11/11;
  `ResearchFreezeTests` 4/4, `PromotionCriteriaTests` 3/3,
  `MultiSourceReplayTests` 6/6, Replay `ArchitectureTests` 1/1,
  `MultiLaneDecisionTraceRegressionTests` 3/3, and
  `MultiSourceDatasetManifestTests` 6/6 passed. Backtesting/Replay.Tests,
  IntegrationTests, and Infrastructure.Tests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Limits and scope: only synthetic OOS fixtures are present. Real OOS
  performance, shadow/paper, or reconciliation evidence remains unknown and is
  not inferred. No package, project graph, canonical source, `graft/`, order,
  production activation, commit, or push was changed. Validator selects
  `P14-M02-T002` READY; implementation stops before T002.

## P14-M02-T002 evidence

- Startup: reread `AGENTS.md`, `docs/PROJECT_SPEC.md`,
  `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and this handoff; Git was clean,
  SDK `10.0.401` matched `global.json`, `P14-M02-T001` was DONE, and
  `tools/validate-docs.ps1` selected only `P14-M02-T002` READY.
- TDD RED: the Release IntegrationTests build completed with zero
  warnings/errors; direct execution of
  `TradingBot.IntegrationTests.PromotionEvidenceBundleTests` discovered 1 test
  and failed at runtime with `NotImplementedException`. RED was a runtime
  behavior failure, not a compile failure or zero-discovery run.
- GREEN: App adds `PromotionEvidenceBundle`, immutable
  `PromotionEvidenceSnapshot`, typed `PromotionEvidenceBundleSources`, and a
  builder bound to the frozen OOS definition/report. Canonical snapshots keep
  producer-native identities separate from content hashes and retain causal
  fields (including null unknowns), provenance, exclusions, model/configuration
  identities, quality/reconciliation reasons, and all unresolved blockers.
  Lane C quality/shadow and research simulation artifacts are checked against
  their linked source/configuration identities. Operational shadow, local-paper,
  and exchange-test evidence remain explicit missing categories; synthetic
  research shadow/simulation evidence is not treated as operational proof.
  `ValidateAgainst` reports affected invalidations without mutating the prior
  bundle, while T003 remains responsible for the review decision.
- Acceptance: the direct GREEN profile passed `PromotionEvidenceBundleTests`
  4/4. Related profiles passed `FrozenOosEvaluationTests` 11/11,
  `ResearchFreezeTests` 4/4, `PromotionCriteriaTests` 3/3,
  `MultiSourceReplayTests` 6/6, `LaneCEvidenceReplayTests` 2/2,
  `MultiLaneDecisionTraceRegressionTests` 3/3, and
  `MultiSourceDatasetManifestTests` 6/6. Affected IntegrationTests,
  Replay.Tests, and Infrastructure.Tests Release builds with `--warnaserror`
  passed with zero warnings/errors; scoped format verification and
  `git diff --check` passed. `tools/validate-docs.ps1` reports
  `P14-M02-T003` as the next READY task.
- Evidence limits: the repository contains only synthetic OOS/shadow/simulation
  fixtures. Real performance, operational shadow, local paper, exchange-test,
  and external reconciliation proof remains unknown and is preserved as
  blockers. No promotion decision, numeric threshold, trading setting, package,
  project-graph, canonical source, `graft/`, commit, or push was changed.
  Implementation stops before `P14-M02-T003`.

## Confirmed state

- Core remains BCL-only. Bar validates positive time span and OHLC bounds; Setup requires its anchor price to lie within the recorded high/low range; Position rejects update times before opening.
- `MarketEventComparison.Compare(previous, candidate)` is a pure diagnostic. Event identity is concrete type + InstrumentId + EventId; identical values yield Duplicate and different values with the same identity yield IdentityConflict. Other comparisons distinguish instrument and EventTime; equal EventTime remains SameEventTime without an EventId tie-breaker.
- `CausalMarketEvent` carries the immutable payload, local receive/availability timestamps, optional independent exchange timestamp, and non-negative stable sequence. Availability cannot precede receive. Sequence assignment remains caller-owned and stream-scoped.
- `CausalEventCursor` applies inclusive payload event-time, availability-time, and stable-sequence bounds. `CausalMarketEventOrdering.Compare` orders eligible events by payload EventTime and stable sequence; equal keys do not imply duplicate payloads. Exchange time remains unrelated to local bounds, and receive time is never substituted for event time.
- `CoreJsonSerializer` provides deterministic compact camelCase JSON for current Core contracts with strict numbers, string enums, rejected unknown members, required constructor parameters, `JsonException` for malformed/domain-invalid input, ordered immutable-list conversion, and `$type` discriminators `trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate` for `MarketEvent` values.
- `LaneCHistoryCapturePipeline` in App stores recovered public depth snapshots, depth updates, trades, and adapter/recovery diagnostics through the existing bounded Parquet capture store. It preserves depth `U`/`u`/`pu`, recovery generation, per-session source sequence, snapshot REST event/transaction times, and a global capture order; depth fails closed after a recovery gap until a ready snapshot is recorded. Details are in [`docs/architecture/lane-c-history.md`](docs/architecture/lane-c-history.md).
- `LaneCFeatureEvaluator` in BCL-only Core reconstructs captured depth to a causal cursor and computes versioned spread, top-N depth, imbalance, trade flow, per-level changes, and explicit wall/absorption candidates. Window, freshness, size bounds, and caller policy are explicit; absent criteria remain unknown. Details are in [`docs/architecture/lane-c-features.md`](docs/architecture/lane-c-features.md).
- `LaneCShadowEvaluator` applies an explicit versioned caller rule table to versioned feature evidence and returns traceable Confirm/Neutral/Veto/InsufficientData results. Veto has priority; missing/unready evidence or an empty rule table is insufficient data. `DecisionEvidence.FromLaneCShadow` preserves identity/cursor/reasons and maps InsufficientData to existing Unknown and Veto to Veto; the Decision Engine can block required unknown Lane C evidence or return Shadow without order/execution effects. No numeric default rules are defined. Details are in [`docs/architecture/lane-c-shadow-decision.md`](docs/architecture/lane-c-shadow-decision.md).
- `LaneCQualityEvaluator` assesses one instrument's Lane C feature input at its exact cursor; the report keeps those causal statuses separate from full-manifest capture counters. It normalizes known diagnostic codes, records unknown codes with safe source/stream/sequence/time provenance, and fails closed for stale, gapped, conflicting, or unavailable required input. `DecisionEvidence.FromLaneCQuality` maps unavailable quality to Unknown and observed failures to Veto. Infrastructure persists the bounded report atomically against the manifest checkpoint, while App composes feature and report builders. Details: [`docs/architecture/lane-c-quality.md`](docs/architecture/lane-c-quality.md).
- `ExchangeEligibilityMetadata` is a venue-neutral immutable metadata contract. It preserves exact decimal filters, official symbol fields, deterministic raw unknown fields, onboarding/delivery times, and separate local receive/availability provenance. `MetadataCandidateFilter.Evaluate` applies only the USDT perpetual/trading metadata gate and returns ordinally ordered candidates with stable exclusion reasons; it consumes no clock, network, feature, ranking, pool-limit, trade, or execution state.
- `ScreenerFeatureInput`, `ScreenerFeatureConfiguration`, and the immutable causal observation contracts define the minimum screener input. `ScreenerCausalSelection` applies the inclusive event-time/availability-time/stable-sequence cursor primitive before ordering observations, and closed bars also require `Bar.CloseTime <= cursor.AsOf`. `ScreenerFeatureCalculator.Calculate` computes only RVOL, price acceleration, ATR expansion, exact quote-volume turnover, spread, and OI anomaly with explicit completeness, caller-supplied TTL freshness, stable quality reasons, warmup, configuration, denominator, input, and overflow states; it uses no thresholds, ranking, persistence, or execution behavior.
- `ScreenerSnapshot` is the immutable Core format-one record for one minimum screener result. It keeps the complete nullable feature configuration/TTL policy, separate `screener-v1` formula version, causal cursor, normalized finite input/provenance observations, and six feature values with completeness/freshness/reasons. Its identity is the lower-case SHA-256 of canonical Core JSON excluding identity and derived properties; no wall clock is included, and equivalent input orderings normalize to the same identity. `SqliteScreenerSnapshotStore` stores the canonical payload with a 32-byte BLOB identity, verifies row metadata, supported versions, canonical JSON, and recomputed identity on reads, and commits idempotent appends before success.
- `StagedSelectionConfiguration`, `StagedSelectionInput`, `StagedSelectionResult`, and the immutable candidate/exclusion evidence contracts define the versioned staged-selection boundary. `UniverseSelectionStage` filters metadata by availability at one shared `CausalEventCursor`, applies the existing metadata-only eligibility gate, collapses exact duplicate screener snapshots, rejects conflicts and future observations, recalculates features from saved inputs, requires every configured Turnover/Spread key to be complete and fresh, and ranks lexicographically with an ordinal InstrumentId tie-break. `CandidatePoolSelectionStage` accepts only the selected Universe membership, carries the same cursor and an explicit prior-stage identity/membership reference, recalculates only the configured six-feature keys, and retains deterministic ranks, reasons, evidence, and capacity exclusions. `RealtimeMonitorSelectionStage` accepts only CandidatePool membership and same-cursor evidence, retains the CandidatePool link, applies the same six-key readiness/ranking rules, and bounds the result at 20. `StagedSelectionChainSnapshot` stores the three stage inputs, evidence, configurations, results, provenance, and exclusions in canonical format-one JSON; it validates the complete Universe→CandidatePool→RealtimeMonitor links and recomputes a lower-case SHA-256 identity without the identity field. Selected and excluded rows retain metadata, snapshot evidence, recalculated values, comparisons, rank, cursor, filter evidence, configuration, and stage provenance; there are no default weights, thresholds, TTLs, persistence, setups, subscriptions, or execution state.
- `RollingIndicatorInput`, `RollingIndicatorConfiguration`, and the immutable rolling result contracts define the BCL-only `rolling-v1` ATR, SMA/EMA/WMA/RMA, and RVOL boundary. `RollingIndicatorCalculator` filters event time, availability, stable sequence, and closed-bar time at one cursor before calculating; explicit `HistoryStart` controls recursive seeds. It uses checked decimal formulas, N/N+1 warmups, duplicate/conflict/gap reasons, zero-denominator and overflow states, canonical ordinal ordering, and retained input/seed provenance. No numeric defaults, TTLs, levels, channels, setups, subscriptions, persistence, or execution were added. Architecture details are recorded in [`docs/architecture/rolling-indicators.md`](docs/architecture/rolling-indicators.md).
- `RiskMathVersion`, `MoneyPerQuantityUnit`, `RiskMathInput`, `RiskMathResult`, and `IRiskMathCalculator` define versioned, deterministic, side-effect-free sizing/exposure contracts shared across replay, paper simulation, and planning. Inputs snapshot the required instrument, direction, equity, risk fraction, entry/stop prices, fee, and slippage. Results include risk budget, loss per unit, raw pre-venue-constraint quantity, and notional exposure. Constructors enforce positive outputs and matching currencies without recomputing formulas; no execution mode, risk decision, sizing implementation, or trading defaults were added. Calculation parity remains P09-M02-T001.
- `FillSimulationVersion`, `FillSimulationAssumptions`, `FillSimulationInput`, `ResearchFill`, `FillSimulationResult`, and `IFillSimulator` define a separate passive research boundary. Inputs are identified hypothetical requests with positive quantity, optional reference price, request time, non-empty fee/slippage/latency/liquidity-queue/partial-fill/intrabar/data-limitation descriptions, and an immutable causal market snapshot. Snapshot events must match the instrument and be available by the request time. Results defensively retain the full input snapshot and only research fills; fill identifiers are unique and total filled quantity cannot exceed the hypothetical request. No fill contract references `OrderIntent`, `RiskDecision`, risk math, executor, or authorization state. Unknown assumptions remain prose, with no numeric defaults. Fill algorithms, replay integration, persistence, and paper execution remain future work.
- `ReplayEnvelope` is an immutable version-1 Core contract carrying its stream ID and complete `CausalMarketEvent`. `SqliteEventJournal` persists the envelope version and reads immutable envelope streams; migration 1 -> 2 assigns version 1 to prior rows. `ReplaySnapshotBuilder` filters with the inclusive causal cursor before ordering by event time and stable sequence. This is a bounded snapshot, not a full replay runner.
- `SqliteOrderIntentJournal` persists the complete Core `OrderIntent` as canonical JSON in the shared SQLite schema. Schema migration 2 -> 3 adds `order_intents` without rewriting `event_journal`; `IntentId` and `ClientOrderId` use BINARY uniqueness. Identical retries return false, identity conflicts roll back, and a successful append returns only after commit. This stage records only an intent awaiting future execution; it adds no adapter, submission, reservation, fill, or reconciliation behavior.
- `PortfolioId`, `PortfolioLimits`, and `PortfolioReservation` are passive immutable BCL-only Core contracts. Limits and reservations require explicit non-negative monetary risk/notional amounts, non-negative position slots, and one currency within each monetary value pair; no trading defaults are supplied.
- `SqliteDatabase` is the shared transactional SQLite initializer. Schema migration 3 -> 4 adds BINARY `portfolios` and `reservations` storage while preserving `event_journal` and `order_intents`; decimal capacity values remain canonical JSON text and are never SQLite REAL values.
- `SqlitePortfolioReservationStore` registers immutable limits, reads persisted limits/reservations and saved risk evidence, and performs authenticated `TryReserve(intent, reservation, riskEvaluation, expectedReconciliationRevision, riskMathCalculator)` in one immediate serializable transaction. It recomputes canonical risk evidence, checks approval and identity parity, rereads checked risk/notional/slot totals, and atomically binds the intent, reservation, and evidence. Results are `Reserved`, `AlreadyReserved`, or `InsufficientCapacity`; conflicts and failures leave no partial mutation.
- `tests/Fixtures/CoreFixtureVectors.cs` remains a linked test-only source in `TradingBot.Core.Tests` and `TradingBot.Replay.Tests`, using only BCL APIs and `TradingBot.Core`.
- [`docs/architecture/core-contract-evidence.md`](docs/architecture/core-contract-evidence.md) records the current fixture identifiers and exact vector matrix. The strings are test examples, not released versions, and no global Core or wire-schema version exists.
- [`docs/architecture/persistence.md`](docs/architecture/persistence.md) records SQLite schema versions 1 through 11, including envelope, intent, portfolio-reservation, operational, capture, screener-snapshot, staged-selection, decision-projection, and risk-evidence migrations, append/read guarantees, bounded segment durability, and evidence limits.
- `SqliteEventJournal`, `SqliteOrderIntentJournal`, `SqlitePortfolioReservationStore`, `SqliteScreenerSnapshotStore`, and `SqliteUniverseSelectionSnapshotStore` share the Infrastructure initializer. They use Microsoft.Data.Sqlite 10.0.12, one non-pooled connection per operation, canonical Core JSON, WAL, `synchronous=FULL`, and BINARY/blob identities. Existing event rows receive envelope format version 1 transactionally; operational observations and checkpoints are durable across restart.
- `ParquetCaptureStore` uses Parquet.Net 6.1.0 plus the same SQLite initializer. One store owns one stream, rejects gaps and bounded-buffer overflow, writes fsynced temporary Parquet files before atomic rename, and commits schema-6 segment metadata/checkpoints transactionally. Reads validate scalar columns against canonical CaptureEnvelope JSON and preserve causal/provenance fields across restart. Schema 6 -> 7 adds screener snapshot rows and schema 7 -> 8 adds staged-selection rows, preserving capture state.
- `CaptureDatasetManifest` is an immutable format-one allowlisted metadata record pinned to one durable capture checkpoint. Its bounded builder reads segments incrementally, records source/instrument/session provenance, event/receive/availability bounds, ordering/configured bounds, and separate gap/exclusion evidence while retaining unknown coverage as `Unknown`. The manifest omits payloads and local paths; atomic JSON persistence rejects a changed checkpoint on reopen.
- `CaptureRetentionPlanner` performs a caller-timed UTC dry-run only. Nullable age/byte policy values remain unknown without defaults; known age uses segment durable time, byte selection orders by durable time then first capture sequence, and every candidate carries reasons. The planner never deletes files or mutates checkpoints.
- `CaptureQualitySnapshot` is an immutable format-one quality record linked to the same manifest/checkpoint. Its bounded builder counts explicit `gap`, `duplicate`, `missing-field`, `clock-skew`, and `stale` evidence once per record, counts unknown diagnostic codes, preserves event-to-receive latency summaries and last receive time, and keeps freshness/provider-silence/clock-skew status `Unknown` when caller thresholds are missing. It does not infer clock skew from latency or gaps from stable sequence.
- `CaptureQualitySnapshotStore` atomically persists/reopens snapshots by manifest identity and checkpoint. `CaptureQualityMetricsPublisher` uses BCL `System.Diagnostics.Metrics` with fixed instrument names, no dynamic identifiers/tags, and an idempotence fingerprint so rereading one snapshot does not double-count counters; observable gauges expose last receive and status codes.
- `Fill`, `PortfolioPositionSnapshot`, and `ReconciliationCheckpoint` are immutable BCL-only Core contracts. Operational observations advance a portfolio revision transactionally, default to `Unreconciled`, preserve unknown/manual markers, and invalidate acknowledgement on every new revision. The retry guard requires acknowledgement and no unresolved markers. Partial fills retain the full reservation; release is idempotent and requires a current acknowledged closed-order/closed-position checkpoint.
- `TradingBot.Exchange.Binance` now owns the public REST contract over injected `HttpClient`. `BinancePublicRestClient` uses current USD-M `/fapi/v1` market-data paths, immutable DTOs, exact decimal parsing, raw Unix-millisecond fields, cancellation, response-size limits, and observable HTTP/API/rate errors. It maps only completed klines to Core `Bar`; recent/aggregate trades, mark/history funding, and current OI map to Core events.
- `ExchangeEligibilityMetadata` is an immutable, venue-neutral Core contract carrying symbol identity, contract/status/assets, onboarding/delivery times, exact decimal filters, official symbol extensions, unknown raw fields, and separate local receive/availability timestamps. `BinanceSymbolFilter` retains `notional`, `multiplierDecimal`, and unknown filter fields/types; exchangeInfo `serverTime` remains wire data and is never used for provenance.
- `docs/architecture/market-data.md` records the 2026-10-12 public endpoint matrix, the 2026-10-16 exchangeInfo/filter facts, weights/auth boundary, DTO/Core mapping decisions, the approved WebSocket `/market` route, and the depth `/public/stream` snapshot/diff contract. T002 owns general market transport; T003 owns depth transport and orderbook recovery.
- `BinanceMarketWebSocketClient` uses an injected transport and `TimeProvider`, parses raw/combined fragmented messages for aggregate trades, klines, tickers, mark-price/funding, and liquidations, and emits immutable session envelopes. Reconnect/resubscribe is bounded and configurable; duplicate, conflict, late, stale, disconnect, gap, parse, and frame-limit diagnostics remain visible. Depth subscriptions are rejected on `/market` and routed through the dedicated depth client.
- `BoundaryRejectionSetupEvaluator` is the pure `boundary-rejection-v1` Lane A family boundary. It consumes a `price-context-v2` result and `feature-quality-v1` evidence at one causal cursor, evaluates LONG/SHORT SETUP-01 conditions on the confirmed channel/local-trend signal bar, compares a caller-supplied positive minimum channel width with the full `Upper - Lower` span, and requires exactly ready and consistent Channel/LocalTrend/HtfContext quality evidence. Ready/NoSetup/Invalidated and explicit unknown states are immutable and serializable; strict close-through is a per-cursor Invalidated observation, equality is NoSetup, and no lifecycle, persistence, risk, order, or execution state is added. Architecture details are recorded in [`docs/architecture/boundary-rejection.md`](docs/architecture/boundary-rejection.md).
- `CenterRetestSetupEvaluator` is the pure `center-retest-v1` Lane A family boundary. It consumes the same `price-context-v2` and `feature-quality-v1` evidence at one causal cursor, evaluates LONG/SHORT SETUP-02 conditions around `PriceContextChannel.Center` on the confirmed signal bar, mirrors the canonical SHORT local-bias condition as `Bear`, and requires exactly ready and consistent Channel/LocalTrend/HtfContext quality evidence. Center Retest has no minimum-width gate and consults no channel width or bands. Ready/NoSetup/Invalidated and explicit unknown states are immutable and serializable; strict close-through is a per-cursor Invalidated observation, equality is NoSetup, and no lifecycle, persistence, risk, order, or execution state is added. Architecture details are recorded in [`docs/architecture/center-retest.md`](docs/architecture/center-retest.md).
- `LaneASetupFixtureTests` is the test-only cross-family Lane A suite. It exercises a 14-case hand-specified matrix across Boundary Rejection and Center Retest, verifies evidence invariants, round-trips price-context/quality snapshots and setup results, replays cursor prefixes through serialized evidence, checks future/no-lookahead behavior, and covers permutation/culture determinism plus V1/execution boundary guards. No production contracts or behavior were added; details are recorded in [`docs/architecture/lane-a-setup-fixtures.md`](docs/architecture/lane-a-setup-fixtures.md).
- `SetupEvidenceSnapshot` is the immutable per-direction `setup-evidence-v1` schema bridging Lane A to Phase 8. It retains the complete `price-context-v2`/`feature-quality-v1` input, one family configuration/observation, a validated causal `SetupEvidenceInputSequence`, and explicit `SetupEvidenceInvalidation` state/ClosedThrough/reasons. It has a lower-case SHA-256 content identity, rejects mismatched or tampered JSON, re-evaluates the selected family in its constructor, and `Replay()` reproduces the identity. It contains no lifecycle, decision, risk, order, or execution state; details are recorded in [`docs/architecture/setup-evidence.md`](docs/architecture/setup-evidence.md).
- The canonical final technical specification was not edited. Graft remains ignored and untouched.

## P13-M01-T003 evidence

- Before T003, the task/dependency status and `pwsh -NoProfile -File tools/validate-docs.ps1` confirmed T003 as the next eligible READY task. TDD RED: focused Core.Tests Release DLL profiles each discovered one test and failed 1/1 at runtime because the Lane C shadow evaluator and `DecisionEvidence.FromLaneCShadow` were absent.
- GREEN: BCL-only versioned rule, result, and metadata contracts plus a pure shadow evaluator preserve input/result identities, rule version, causal cursor, matched rule IDs, and reason codes. Caller tables have no default thresholds; empty/no-evaluable rules or unavailable features yield InsufficientData, and Veto takes priority over Confirm. Decision evidence maps InsufficientData to Unknown and Veto to Veto; required Unknown blocks, while accepted evidence can yield Shadow without order or execution side effects. Details: [`docs/architecture/lane-c-shadow-decision.md`](docs/architecture/lane-c-shadow-decision.md).
- Direct Release DLL profiles passed: LaneCShadowTests 4/4, LaneCDecisionTests 3/3, DecisionEvidenceMetadataTests 5/5, DecisionReasonCodesTests 4/4, DecisionProjectionSnapshotTests 4/4, SetupLifecycleTests 12/12, CoreBoundaryTests 2/2, ArchitectureTests 1/1, and LaneCFeatureTests 8/8. Core.Tests Release build with `--warnaserror` completed with zero warnings/errors; scoped format verification, docs validation, and `git diff --check` passed. Validator now selects P13-M02-T001 READY.
- No full suite, package/project-graph change, canonical source change, `graft/` edit, commit, or push was performed. P13-M01 is complete; stop before P13-M02-T001.

## P13-M02-T001 evidence

- Startup status and the validator selected T001; `P13-M01-T003` was DONE. TDD RED used direct Release xUnit DLL profiles: quality report 2/2 runtime failures because the report builder was absent; quality/Decision 2/2 because evaluator/mapping were absent; App composition 1/1 because the App service was absent. A focused follow-up test for generic conflict counting failed at runtime 1/3 before the counter was implemented.
- GREEN adds immutable versioned Core quality evidence linked to feature input identity and causal cursor, with explicit provider policy, per-category signal counts, safe unknown diagnostic provenance, deterministic identity and Decision mapping. Infrastructure produces bounded manifest/checkpoint reports, verifies durable feature rows and identity links, stores/reopens reports atomically, and separates causal quality from whole-manifest counters. `CaptureQualitySnapshot` now counts known conflicts and reads legacy payloads with absent `conflictCount`; its current freshness uses receives at or before `asOf`. App composes feature and report evaluation. Details: [`docs/architecture/lane-c-quality.md`](docs/architecture/lane-c-quality.md).
- Acceptance passed: LaneCQualityDecisionTests 7/7; LaneCQualityReportTests 3/3; LaneCQualityReportCompositionTests 1/1; CaptureQualitySnapshotTests 4/4; CaptureDatasetManifestTests 7/7; ParquetCaptureStoreTests 4/4; LaneCFeatureTests 8/8; LaneCShadowTests 4/4; LaneCDecisionTests 3/3; DecisionEvidenceMetadataTests 5/5; CoreBoundaryTests 2/2; ArchitectureTests 1/1; LaneCHistoryCaptureTests 3/3. Core.Tests, Infrastructure.Tests, and IntegrationTests Release builds passed with `--warnaserror`, zero warnings/errors. Scoped format, docs validation, and `git diff --check` passed. Validator now selects P13-M02-T002 READY. No full suite, package/project-graph, canonical source, or `graft/` changes.

## P13-M02-T002 evidence

- Preflight rechecked task dependencies and validator after T001; T002 was the first READY task and T001 was DONE. TDD RED: the focused Release Infrastructure.Tests DLL selected 4 `LaneCCaptureExclusionTests` and all 4 failed at runtime because the ledger and safe-reference behavior were absent. Follow-up profile REDs confirmed read-time segment validation failed 1/1 and complete-evidence ordering failed 1/1.
- GREEN adds a versioned Infrastructure ledger linked to quality report identity, manifest id, checkpoint, and unchanged coverage. Exclusion entries retain safe source/instrument scope, stable reason codes, provenance, and nullable known bounds. Builder and readback validate manifest segment/sequence references; complete-evidence ordering and exact-entry deduplication are deterministic. Retention records the existing DryRun result with its nullable policy, as-of time, candidates, and reasons. The atomic JSON store reopens with manifest/report/checkpoint/coverage/reference checks. Candidate retention does not remove data or change checkpoints. Details: [`docs/architecture/lane-c-exclusions.md`](docs/architecture/lane-c-exclusions.md).
- Acceptance passed: `LaneCCaptureExclusionTests` 6/6, `CaptureDatasetManifestTests` 7/7, `LaneCQualityReportTests` 3/3, `CaptureQualitySnapshotTests` 4/4, and `ParquetCaptureStoreTests` 4/4. Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors. Scoped format verification, docs validator, and `git diff --check` passed; validator selects P13-M02-T003 READY.
- No full suite, delete/checkpoint mutation, package/project-graph, canonical source, or `graft/` change. Recheck status, dependencies, Git, and validator before starting T003.

## P13-M02-T003 evidence

- Preflight after T002 rechecked Git status, task dependencies, and the documentation validator; T003 was first READY and T002 was DONE. TDD RED: a direct Release IntegrationTests DLL run selected `LaneCEvidenceReplayTests` and failed 1/1 at runtime because App had no verifiable Lane C evidence bundle builder.
- GREEN adds versioned immutable `LaneCEvidenceBundle` in App, validating manifest/checkpoint, quality report, exclusion ledger, feature, shadow, and Decision evidence identities and causal cursors. The bundle is deterministic canonical evidence only and has no order/execution action. `LaneCEvidenceReplayTests` captures fixed synthetic Binance snapshots, depth updates, and trades, then adds gap/recovery and identity-conflict diagnostics; equal event times retain distinct stable sequences and delayed availability is excluded from the earlier cursor. Capture, quality report, and exclusion ledger are reopened before repeated replay and serialization comparison. Expected results cover recovered state with historical gap, stale cursor, conflict, exclusions, InsufficientData/Unknown decision, stable reasons and unchanged capture checkpoint. Details: [`docs/architecture/lane-c-evidence-replay.md`](docs/architecture/lane-c-evidence-replay.md).
- Acceptance passed: LaneCEvidenceReplayTests 2/2, LaneCHistoryCaptureTests 3/3, LaneCQualityReportCompositionTests 1/1, LaneCFeatureTests 8/8, LaneCShadowTests 4/4, LaneCDecisionTests 3/3, LaneCQualityDecisionTests 7/7, CoreBoundaryTests 2/2, Core ArchitectureTests 1/1, LaneCCaptureExclusionTests 6/6, LaneCQualityReportTests 3/3, CaptureQualitySnapshotTests 4/4, CaptureDatasetManifestTests 7/7, and ParquetCaptureStoreTests 4/4. Core.Tests, Infrastructure.Tests, and IntegrationTests Release builds with `--warnaserror` passed with zero warnings/errors; scoped format verification, docs validator, and `git diff --check` passed. The validator now selects P14-M01-T001 READY.
- Scope: no full suite, order/execution action, capture checkpoint mutation, package/project-graph, canonical source, `graft/`, commit, or push. Stop before P14-M01-T001; repeat status/dependency/validator preflight before that block.

## P11-M01-T001 evidence

- Startup: the PowerShell 7 documentation validator selected `P11-M01-T001` as the first READY task; `P10-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build first failed on the missing `RiskEvaluationInput` and `RiskEvaluationConfiguration` contracts. After a compile-only skeleton, direct xUnit execution of `TradingBot.Core.Tests.RiskEngineTests` failed 3/3 for the expected unimplemented behavior.
- GREEN: Core adds immutable `RiskEvaluationInput`, `RiskEvaluationConfiguration`, `RiskEvaluationResult`, explicit health/kill-switch/cooldown/protection evidence records, caller-owned risk limit and portfolio-usage records, stable `RiskReasonCodes`, and pure `RiskEvaluationEngine`. It accepts only a matching `DecisionProjectionSnapshot` whose outcome is `Allow`; validates the calculator version and full `RiskMathInput` snapshot; reuses the raw `RiskMathResult`; checks caller-supplied daily loss for its explicit period plus candidate risk; checks per-trade, portfolio risk, notional, margin, and slot capacities with checked decimal arithmetic; rejects currency and identity mismatches; and never reduces a proposed size. Missing protection/cooldown remains blocked, so T001 cannot produce full approval.
- Verification: direct xUnit `RiskEngineTests` passed 11/11; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes` and `git diff --check` passed. T002 is now READY and must be reselected before implementation.
- Scope: changes are limited to Core risk evaluation contracts/engine, focused Core tests, `docs/TASKS.md`, this handoff, and the risk-model architecture note. No portfolio reservation integration, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P11-M01-T002 evidence

- Startup: after T001, the validator selected `P11-M01-T002` as the first READY task; `P11-M01-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build passed after the T001 implementation, then direct xUnit `RiskEngineProtectionTests` failed 3/10 for the missing aggregate level-link rejection and approval behavior.
- GREEN: `RiskProtectionEvidence` now validates every explicit stop/TP level against aggregate instrument, direction, setup/method link, source/version, freshness, and causal cursor. `RiskEvaluationEngine` validates stop equality with the shared `RiskMathInput`, loss/profit side for LONG/SHORT, fresh causal evidence, and explicit cooldown state. Unknown/stale/future/conflicting/mismatched evidence blocks; active cooldown blocks before its supplied expiry and clear state permits at the boundary. Approval remains a passive `RiskDecision` evidence record.
- Verification: direct xUnit `RiskEngineProtectionTests` passed 10/10; adjacent `RiskEngineTests` passed 11/11; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes` and `git diff --check` passed. T003 is now READY and must be reselected before implementation.
- Scope: changes are limited to Core risk contracts/engine, focused Core tests, `docs/TASKS.md`, and this handoff. No reservation, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P11-M01-T003 evidence

- Startup: after T002, the validator selected `P11-M01-T003` as the first READY task; `P11-M01-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source remains immutable.
- TDD RED: the focused Core.Tests Release build passed after T002, then direct xUnit `RiskEngineFailClosedTests` first failed 1/7 because a tampered `RiskDecision` identity/version link was accepted by `RiskEvaluationResult` construction; the added calculator-version failure fixture then escaped before its guard.
- GREEN: result construction now validates evaluator/configuration version and deterministic `risk-{EvaluationId}` plus source Decision identity, and rejects retained risk-math output whose version or complete input snapshot differs. The pure engine fail-closes unknown/missing policies and health, stale/future/conflicting/mismatched evidence, enabled/unknown kill switch, currency mismatch, checked-capacity overflow, calculator version/input/exception, and causal lookahead. Kill-switch rejection occurs before calculator invocation; LONG/SHORT side checks, exact limits, strict JSON, and absence of execution/reservation surfaces are covered.
- Verification: direct xUnit `RiskEngineFailClosedTests` passed 8/8; adjacent `RiskEngineTests` passed 11/11 and `RiskEngineProtectionTests` passed 10/10; Core Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --no-restore --verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed. Expected next READY tasks are `P11-M02-T001` and `P13-M01-T001`; implementation stopped at P11-M01.
- Scope: changes are limited to Core risk contracts/engine, focused Core tests, `docs/architecture/risk-model.md`, `docs/TASKS.md`, and this handoff. No reservation integration, execution, package, project-graph, P13, or canonical source change was made; no commit or push was performed.

## P06-M02-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FeatureQualityEvaluatorTests"` failed with the expected missing `FeatureQualityEvaluation`, `FeatureQualityEvaluator`, policy, evidence, and enum contracts before implementation.
- `FeatureQualityEvaluator` is an independent BCL-only quality boundary for `rolling-v1` and `price-context-v2`. It preserves calculator formulas and serialized contracts, reads no clock, accepts nullable rolling/setup/HTF/price-volume TTLs without defaults, computes market-data age from bar close/event time and availability age from local availability against the result cursor, and treats equality with TTL as fresh and values above TTL as stale. Missing and non-positive TTLs remain unknown.
- Immutable per-feature evidence retains original state/completeness, source formula version, derived freshness, both ages, TTL, known/required counts, bounded coverage, duplicate/gap/conflict/incomplete reasons, and fail-closed readiness. Exact duplicates are canonicalized once while their diagnostics remain visible; stale, unknown, incomplete, gapped, or conflicting inputs cannot produce ready quality evidence.
- TDD GREEN: focused FeatureQualityEvaluatorTests passed 3/3; affected RollingIndicatorTests and PriceContextFeatureTests passed 25/25; Core.Tests Release build with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed after normalizing new files to repository CRLF/encoding. Architecture details are recorded in [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md).
- `pwsh -NoProfile -File tools/validate-docs.ps1` is required immediately after this transition; the expected next validator-selected task is `P06-M02-T002`. No persistence, metrics, setup, execution, Phase 7, or Lane C behavior was added.

## Verification evidence

- Startup protocol: worktree was clean at `7c46c1b`; P03-M01-T002 was the first `READY` task and P03-M01-T001 was `DONE`.
- Unlocked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx` — exit 0.
- Locked restore: `C:\temp\dotnet10\dotnet.exe restore TradingBot.slnx --locked-mode` — exit 0.
- RED evidence: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteEventJournalTests"` failed before implementation with the expected missing `SqliteEventJournal` compile errors.
- Release Infrastructure.Tests build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Infrastructure tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Infrastructure.Tests/TradingBot.Infrastructure.Tests.csproj -c Release --no-build --no-restore` — 10 passed, 0 failed.
- Core boundary/project-graph build: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` — exit 0, zero warnings and errors.
- Core boundary/project-graph tests: `C:\temp\dotnet10\dotnet.exe test tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CoreBoundaryTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~ProductionProjectGraphMatchesApprovedModuleBoundaries"` — 4 passed, 0 failed.
- Changed C# format: `C:\temp\dotnet10\dotnet.exe format TradingBot.slnx --no-restore --verify-no-changes --include src/TradingBot.Infrastructure/SqliteEventJournal.cs tests/TradingBot.Infrastructure.Tests/SqliteEventJournalTests.cs` — exit 0.
- Documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M02-T001.
- P03-M01-T002 locked restore — exit 0; Core focused tests — 3 passed; Infrastructure tests — 12 passed; Replay tests — 5 passed; Release warnings-as-errors builds for Core.Tests, Infrastructure.Tests, and Replay.Tests — zero warnings/errors; scoped C# format — exit 0.
- P03-M01-T002 documentation validation: `pwsh -NoProfile -File tools/validate-docs.ps1` — exit 0; phases=19, milestones=41, tasks=119, ready=P03-M01-T003.
- `git diff --check` — exit 0.
- Canonical source SHA-256: `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- GitHub Actions run [`37671835797`](https://github.com/KirillNedoboy/TrendBot/actions/runs/37671835797) for the preceding commit completed with failure on `Verify formatting` on both Windows and Ubuntu. The CI correction remains outside this task.

## P03-M01-T003 evidence

- IntegrationTests adds a real SQLite write-close-reopen replay proof through SqliteEventJournal and ReplaySnapshotBuilder.
- Restart comparison covers envelopes, stable-sequence storage order, canonical JSON, causal snapshot order, and a deterministic projection string.
- Focused recovery cases cover idempotent repeat, same-key conflict, sequence gaps, late event time, unavailable events, corrupted payloads, write lock timeout, and no partial append after failure.
- Locked IntegrationTests restore passed; Release IntegrationTests build with warnings as errors passed with zero warnings and errors; all 5 IntegrationTests passed; scoped C# format verification passed.

## P03-M02-T001 evidence

- `SqliteOrderIntentJournal` opens the shared file-backed schema, appends canonical `OrderIntent` JSON transactionally, returns false for identical retries, rejects both IntentId and ClientOrderId conflicts without mutation, and finds intents after restart.
- Targeted tests cover all intent fields and decimal/null precision, schema 2 -> 3 migration with event preservation, shared event/intent use, rollback, and a one-second busy-writer failure with no partial row.
- Locked restore passed; Release Infrastructure.Tests build with warnings as errors passed with zero warnings and errors; focused intent tests passed 6/6 and all Infrastructure tests passed 18/18; scoped C# format verification passed.
- Documentation validation passed with first READY task P03-M02-T002; `git diff --check` passed; canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P03-M02-T002 evidence

- Core adds immutable `PortfolioId`, `PortfolioLimits`, and `PortfolioReservation` contracts with explicit caller-supplied monetary-risk, notional-exposure, and position-slot values. Matching currencies, non-negative values, strict JSON round trips, and immutability are covered by `PortfolioReservationContractTests`.
- Infrastructure extracts shared SQLite initialization and migration 3 -> 4. The migration creates BINARY `portfolios` and `reservations` tables while preserving existing `event_journal` and `order_intents` rows. `SqlitePortfolioReservationStore` persists canonical JSON only, keeps identity columns BINARY, and writes an existing `OrderIntent` with its reservation atomically.
- `SqlitePortfolioReservationStoreTests` cover exact capacity boundaries, independent-connection restart recovery, duplicate idempotence, portfolio/reservation/intent conflicts, missing portfolios, currency mismatch, rollback under a held writer, and separate barrier races for risk, notional, and position slots. Focused reservation tests passed 11/11; full Infrastructure.Tests passed 29/29.
- Release IntegrationTests and Replay.Tests builds passed with warnings as errors; IntegrationTests passed 5/5 and Replay.Tests passed 5/5. Core.Tests passed 62/62, including the new contract tests; Release Core.Tests and Infrastructure.Tests builds passed with zero warnings/errors; scoped C# format verification passed.
- Locked restore, documentation validation, `git diff --check`, and canonical source hash verification passed. The canonical `FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P03-M02-T003 evidence

- Core adds immutable `Fill`, `PortfolioPositionSnapshot`, and `ReconciliationCheckpoint` contracts. Fill values retain intent/instrument identity, positive quantity and price, optional fee, event/receive/availability/exchange timestamps, and stable sequence. Snapshots retain Core `Position` values with known intent ownership or explicit manual/unknown origin. Checkpoints retain monotonic revision, open intents/positions, unknown/manual markers, and explicit `Unreconciled`/`Acknowledged` status.
- Infrastructure schema migration 4 -> 5 adds the reservation release marker, fills, immutable position snapshots, and current reconciliation checkpoints while preserving event, intent, portfolio, and reservation rows. `RecordObservation` persists observations and the next checkpoint atomically using checked application decimal arithmetic; duplicate observations are idempotent and conflicts, aggregate overflow, stale revisions, and gaps leave no partial mutation.
- `SqliteOperationalStateTests` cover restart persistence, duplicate idempotence, acknowledgement invalidation, fill identity conflict, aggregate overflow, stale revision rejection, unknown/manual retry blocking, full reservation retention through a partial fill, acknowledged closed-state release, repeated release, and 4 -> 5 migration preservation.
- Locked restore passed. Release warnings-as-errors builds passed with zero warnings/errors for Core.Tests, Infrastructure.Tests, IntegrationTests, and Replay.Tests. Post-format suites passed: Core.Tests 64/64, Infrastructure.Tests 35/35, IntegrationTests 5/5, Replay.Tests 5/5. Scoped C# format, docs validation, `git diff --check`, and canonical source SHA verification passed.

## P04-M01-T001 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceRestClientTests` initially failed with the expected missing `BinancePublicRestClient` compile error before production implementation.
- `BinancePublicRestClient` and immutable REST DTOs cover exchangeInfo, server time, price/24-hour tickers, klines, recent/aggregate trades, mark/funding, funding history/info, and current open interest. String decimals remain exact; raw exchange milliseconds remain available; open klines stay outside Core `Bar`.
- Fixture evidence covers object/array response shapes, current paths/query values, decimal precision, raw times, kline closure, buyer-maker trade side, Core mapping, caller cancellation, HTTP 429, Binance error code/message, and response-size limits.
- Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 7/7; scoped format verification passed; documentation validation passed with first READY task `P04-M01-T002`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M01-T002 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceWebSocketClientTests` initially failed with the expected missing WebSocket contract/client types before production implementation.
- `BinanceMarketWebSocketClient` keeps `/market` as the required route, sends live `SUBSCRIBE` commands after every connection, optionally sets combined mode, assembles fragmented text/binary messages under configured frame/message limits, and parses the five public stream kinds. `ClientWebSocketTransport` wraps BCL `ClientWebSocket` with keepalive.
- Immutable stream envelopes preserve raw JSON, event/exchange times, receive/availability times, a session identifier, and stable per-session sequence. Kline updates use the candle open time as identity, so open updates can evolve without identity conflicts; only closed updates map to Core `Bar`. Liquidations are dedicated events and never become Core `Trade` values.
- Focused fixtures cover duplicate identity, different-value identity conflict, late event time, stale event time, aggregate-trade gaps, disconnect/reconnect/backoff/resubscribe, heartbeat, 24-hour session policy, raw/combined/fragmented frames, cancellation, causal fields, and liquidation separation. Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 15/15; scoped format verification passed.
- Documentation validation passed with first READY task `P04-M01-T003`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M01-T003 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Exchange.Binance.Tests\TradingBot.Exchange.Binance.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BinanceDepthAndOrderBookTests` initially failed with the expected missing depth snapshot, stream, recovery, and transport-factory contracts before production implementation.
- The depth REST adapter covers `/fapi/v1/depth`, official limits, exact-decimal levels, raw `lastUpdateId`/`E`/`T`, and local HTTP receive/availability provenance. `BinanceDepthWebSocketClient` is pinned to `/public/stream`, parses raw/combined fragmented depth updates, preserves causal envelope fields, bounds frames/messages/deduplication/reconnects, emits diagnostics, and creates a fresh transport per session through the test seam.
- `BinanceLocalOrderBookRecovery` uses bounded snapshot/diff buffering, overlap alignment, `pu`/range contiguity checks, bounded snapshot levels and retries, fail-closed recovering states, immutable sorted ready states, and snapshot/update provenance. The existing `/market` client rejects depth subscriptions.
- Release Exchange.Tests build with warnings as errors passed with zero warnings/errors; all Exchange.Tests passed 21/21; scoped format verification passed. Documentation validation passed with `P04-M01-T003` complete and `P04-M02-T001,P04-M03-T001` listed READY; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M03-T001 evidence

- TDD RED: the Core capture and Infrastructure persistence test projects initially failed with the expected missing `CaptureEnvelope`/`CaptureProvenance` and `ParquetCaptureStore`/schema-6 contracts before implementation.
- Core adds immutable CaptureEnvelope v1, provenance, diagnostics, causal timestamps, stable/capture sequences, and depth/trade/diagnostic-only validation. Infrastructure adds Parquet.Net 6.1.0 persistence with explicit row, buffer, and byte bounds; fsynced temporary files, atomic rename, serializable segment/checkpoint commit, cleanup on failed publish, configurable flush-on-dispose, and restart validation. SQLite migration 5 -> 6 creates `capture_segments` and `capture_checkpoints` while preserving prior rows.
- Capture tests cover canonical JSON, immutable causal/provenance fields, bounded multi-segment writes, oversize-file rejection without published metadata/file, sequence and buffer bounds, restart recovery, and 5 -> 6 migration preservation. Integration coverage closes and reopens the store with a public trade capture and verifies its checkpoint/provenance. No strategy, order, or execution state is included.
- Unlocked and locked restore passed. Release Core.Tests, Infrastructure.Tests, IntegrationTests, and Exchange.Tests builds with warnings as errors passed with zero warnings/errors. Release suites passed Core 67/67, Infrastructure 39/39, Integration 6/6, and Exchange 21/21. Scoped C# format verification passed; documentation validation passed with `P04-M02-T001,P04-M03-T002` listed READY; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P04-M03-T002 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CaptureDatasetManifestTests"` initially failed with the expected missing manifest, bounded reader, and retention types before implementation.
- `CaptureDatasetManifest` is an immutable format-one allowlist containing the fixed stream/checkpoint, segment bounds and sizes, source/instrument/session provenance, observed causal-time bounds, ordering/configured limits, explicit unknown coverage, and separate gap/exclusion evidence. `CaptureDatasetManifestBuilder` aggregates durable Parquet segments through an explicit incremental segment/record bound; it omits raw payloads and absolute segment paths and rejects source userinfo, sensitive query keys, absolute paths, and listenKey-like values.
- `CaptureRetentionPlanner` preserves nullable unknown limits, accepts only caller-supplied UTC `asOf`, uses durable segment time and deterministic byte tie-breaks, reports candidate reasons, and returns `Unknown` with no candidates when both limits are unknown. Dry-runs expose no deletion or checkpoint mutation API. Atomic manifest persistence and reopen are pinned to the durable checkpoint.
- Focused `CaptureDatasetManifestTests` passed 7/7. Release Infrastructure.Tests build with warnings as errors passed with zero warnings and errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No data deletion, credential, private API, or Core/schema change was performed.

## P04-M03-T003 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CaptureQualitySnapshotTests"` initially failed with the expected missing `CaptureQualitySnapshotBuildOptions` type before implementation.
- `CaptureQualitySnapshotBuilder` rebuilds deterministic quality evidence for the same manifest/checkpoint using bounded durable reads. It counts documented `gap`, `duplicate`, `missing-field`, `clock-skew`, and `stale` signals once per record even when represented by both a quality flag and diagnostic code; unknown diagnostic codes remain visible. Latency and last receive are observed values only, and sequence gaps or latency do not infer clock skew.
- Caller-supplied UTC `asOf` and nullable freshness/provider-silence/clock-skew thresholds produce explicit statuses; missing thresholds stay `Unknown`, empty/no-new records produce provider silence when a threshold is known, and no numeric project TTL/default is introduced. Atomic snapshot persistence links manifest ID/checkpoint, and fixed BCL meters publish counters once per snapshot fingerprint plus low-cardinality observable gauges.
- Focused `CaptureQualitySnapshotTests` passed 4/4, including equal/late times, duplicate evidence, unknown code/thresholds, empty/no-new silence, restart rebuild, and `MeterListener` measurements. The focused capture integration test passed 1/1. Release Infrastructure.Tests and IntegrationTests builds with warnings as errors passed with zero warnings and errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Core contract, SQLite schema, deletion, credential, private API, or order behavior was added.

## P04-M02-T001 evidence

- `docs/architecture/private-protocol.md` catalogs all current USDⓈ-M private REST methods in the official Account and Trade sections, including Algo methods, plus the three listenKey methods and the current user-data event inventory.
- Each endpoint row records method, full production URL, confirmed parameters and response fields or `UNKNOWN`, security/signature, published IP/order limits or `UNKNOWN`, documented errors/time constraints, official source URL, and checked date 2026-10-15. The New Algo Order narrative versus `type` enum conflict is explicitly `NEEDS_VALIDATION`; no client behavior is inferred from it.
- Official source review, `pwsh -NoProfile -File tools/validate-docs.ps1`, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, private credential, or order was used.

## P04-M02-T002 evidence

- Test-only synthetic fixtures and helpers in `tests/TradingBot.Exchange.Binance.Tests` preserve the confirmed `ALGO_UPDATE` envelope fields `e`, `E`, `T`, and opaque object `o` from the official schema checked 2026-10-15. Nested `o` content remains opaque where the official schema does not confirm it.
- Fixtures keep the algo identifier separate from the session/stable-sequence event identity, retain exchange `E`/`T` and local receive/availability timestamps, and cover repeated payloads, same-algo state transitions, equal and late exchange timestamps, unknown fields/status values, and malformed confirmed fields. No global identifier uniqueness or transport/auth behavior is assumed.
- Focused `BinanceAlgoProtocolFixtureTests` passed 5/5. Exchange.Tests Release build with `--warnaserror` passed with 0 warnings and 0 errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, credential, or order was used.

## P04-M02-T003 evidence

- `docs/architecture/private-protocol.md` now records the official General Info and user-data freshness/error rules checked 2026-10-15, including explicit synthetic test intervals, project TTL `UNKNOWN`, listenKey expiry/disconnect handling, clock skew, rate/auth diagnostics, HTTP 503 variants, and reconciliation for unknown outcomes. It records that an absent order response does not prove non-execution and that ordinary-order guarantees are not transferred to Algo without an official source.
- `BinancePrivateProtocolEvidenceEvaluator` is test-only and returns diagnostic reasons only. `BinancePrivateProtocolEvidenceTests` covers incomplete/stale evidence, unknown statuses, disconnect/listenKey expiry, clock skew, rate limits, authentication failures, ambiguous outcomes, missing order responses, and 503 unknown/service-unavailable/internal/throttled variants. Combined Algo/evidence fixture tests passed 15/15.
- Exchange.Tests Release build with `--warnaserror` passed with 0 warnings and 0 errors; scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No Binance API endpoint, credential, or order was used.

## Scope limits

- P03-M01-T002 adds only the versioned replay envelope, SQLite schema 1 -> 2 migration, deterministic bounded Backtesting snapshot, focused tests, architecture notes, and this handoff.
- P03-M02-T001 adds only durable order-intent state; P03-M02-T002 adds only passive portfolio limits/reservations, shared schema 3 -> 4 migration, and atomic intent-plus-reservation persistence; P03-M02-T003 adds only passive operational fills, position snapshots, reconciliation checkpoints, and guarded full-reservation release. Binance calls, credentials, venue adapters, execution, StrategyRun, ConfigVersion, capture manifests, retention policy, and recovery policy remain future tasks.
- P04-M01-T001 adds only public REST market-data transport/normalization and fixtures. It does not add private endpoints, credentials, orderbook/depth recovery, WebSocket transport, strategy behavior, retries, or orders.
- P04-M01-T002 adds only public WebSocket parsing, bounded reconnect/resubscribe, causal envelopes, diagnostics, and fixtures. P04-M01-T003 adds only public depth REST/`/public/stream` transport, bounded local snapshot/diff recovery, provenance, and fixtures; it adds no private API, strategy, orders, or capture persistence.
- P04-M03-T001 adds only passive Core capture envelopes and bounded depth/trade/diagnostic Parquet persistence with schema-6 metadata/checkpoints. It adds no strategy, orders, execution, private API, or capture manifest/retention policy.
- P04-M03-T002 adds only Infrastructure manifest/retention metadata and test fixtures. It adds no Core contract, SQLite schema migration, capture deletion, exporter, API call, credential, strategy, order, or execution behavior.
- P04-M02-T001 adds only the official-source private protocol catalogue and user-data event inventory; it adds no private transport, credentials, signing implementation, order, or execution behavior.
- P04-M02-T002 adds only synthetic Algo Service/`ALGO_UPDATE` fixtures and test-only parsing/identity helpers; it adds no production transport, authorization, private API call, order, or Core contract.
- P04-M02-T003 adds only a test-only freshness/error diagnostic evaluator and evidence matrix documentation; it adds no execution permission, retry logic, private transport, credential, order, or Core contract.
- P05-M01-T001 adds only immutable exchange eligibility metadata, exact-decimal Binance filter extensions, deterministic unknown-field retention, local receive/availability provenance, Core mapping, official-source notes, and focused fixtures. P05-M01-T002 adds only the metadata-only candidate filter and deterministic exclusion fixtures. These tasks add no screener/features, ranking, pool limits, persistence, order, or execution behavior.
- P05-M02-T001 adds only the immutable causal screener input/configuration contracts, shared cursor selection, and six pure minimum feature formulas with explicit warmup/unknown reasons. P05-M02-T002 adds only caller-supplied TTL freshness/completeness metadata and deterministic causal quality handling for duplicates, conflicts, gaps, and late observations. These tasks add no ranking, pool limits, snapshots, order, or execution behavior.
- Remote CI success remains unverified.

## P05-M01-T001 evidence

- TDD RED: the new Core metadata and Binance exchangeInfo fixtures initially failed with the expected missing `ExchangeEligibilityMetadata`, filter extension, and provenance members before production implementation.
- Core metadata retains exact decimal `PRICE_FILTER`, `LOT_SIZE`, and `MIN_NOTIONAL` values, immutable sorted unknown fields, official symbol extensions, onboarding/delivery timestamps, and separate local receive/availability timestamps. The Binance adapter captures local provenance around `/fapi/v1/exchangeInfo`; it retains raw `serverTime` without using it as a clock source and exposes a Core metadata mapping path.
- Official source facts checked 2026-10-16 are recorded in [`docs/architecture/market-data.md`](docs/architecture/market-data.md): exchangeInfo weight 1, public/no-key boundary, documented filter fields and zero PRICE_FILTER semantics, precision fields not being tick/step substitutes, and freshness/TTL remaining `UNKNOWN`.
- `C:\temp\dotnet10\dotnet.exe restore tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj --locked-mode` and the equivalent Exchange.Tests restore both exited 0. Release warnings-as-errors builds for both test projects exited 0 with 0 warnings and 0 errors. Focused `ExchangeEligibilityMetadataTests` passed 2/2; focused exchangeInfo parser/provenance tests passed 2/2. Scoped `dotnet format TradingBot.slnx --no-restore --verify-no-changes --include` over all changed C# files exited 0.
- The T001 gate passed before T002 began: `pwsh -NoProfile -File tools/validate-docs.ps1` confirmed `P05-M01-T002` READY; `git diff --check` and canonical source SHA-256 `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54` passed.

## P05-M01-T002 evidence

- TDD RED: the new metadata filter fixtures initially failed with the expected missing `MetadataCandidateFilterResult` and `MetadataCandidateFilter` types before production implementation.
- `MetadataCandidateFilter.Evaluate` includes only `PERPETUAL` + `TRADING` symbols with `USDT` quote and margin assets. It requires and validates `PRICE_FILTER`, `LOT_SIZE`, and `MIN_NOTIONAL`, validates present recognized optional filters, retains additional filters as unknown metadata, and excludes malformed or duplicate/conflicting metadata with stable ordinal reasons. Zero `PRICE_FILTER` values follow the official disabled-rule semantics.
- The deterministic Core fixtures cover repeatability, shuffled inputs, ordinal symbol ordering, cultures, USDT/USDC and contract/status gates, missing/conflicting filters, zero `PRICE_FILTER` values, recognized optional fields, and additional unknown filters. Focused Core metadata/filter tests passed 7/7; focused Binance exchangeInfo parser/provenance/receive-time tests passed 3/3. A regression test first reproduced the pre-request receive-time bug, then passed after receive time was captured when the HTTP document became available.
- `C:\temp\dotnet10\dotnet.exe restore tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj --locked-mode` and the equivalent Exchange.Tests restore both exited 0. Release warnings-as-errors builds for both test projects exited 0 with 0 warnings and 0 errors. Scoped `dotnet format TradingBot.slnx --no-restore --verify-no-changes --include` exited 0. `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `ready=P05-M02-T001`; `git diff --check` exited 0; and the canonical source SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P05-M02-T001 evidence

- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ScreenerFeatureCalculatorTests` initially failed with the expected missing screener contract types before production implementation.
- `ScreenerFeatureInput` preserves source/instrument identity, event/receive/availability timestamps, stable sequence, immutable closed bars with exact quote volume, ready bid/ask observations, and OI observations. `ScreenerFeatureConfiguration` requires explicit timeframe, lookbacks, and formula version; omitted or invalid values remain `UnknownConfiguration` with no numeric defaults. `ScreenerCausalSelection` filters by the inclusive event-time/availability-time/stable-sequence cursor before deterministic ordering.
- `ScreenerFeatureCalculator.Calculate` is pure and returns exactly RVOL, price acceleration, ATR expansion, quote-volume turnover, spread, and OI anomaly. The implementation uses decimal without trading rounding, applies the specified `N+1`, `2L+1`, `2N+1`, `K`, one-quote, and `M+1` warmups, and reports warmup, input, zero-denominator, and overflow reasons. No ranking, threshold, freshness, persistence, or execution behavior was added.
- Focused screener fixtures passed 8/8; full `TradingBot.Core.Tests` passed 82/82. Locked Core.Tests restore exited 0; Release Core.Tests build with `--warnaserror` exited 0 with zero warnings/errors; scoped format verification exited 0. Architecture details are recorded in [`docs/architecture/screener-features.md`](docs/architecture/screener-features.md).
- Documentation validation, `git diff --check`, and canonical source SHA-256 verification were run after the task transition; remote CI remains unverified.

## P05-M02-T002 evidence

- `ScreenerFeatureValue` now retains nullable value, completeness, freshness, age, latest required availability, and immutable stable reasons. `ScreenerFreshnessPolicy` accepts only caller-supplied positive TTLs by default, feature family, or source; missing TTL remains `Unknown`, age exactly equal to TTL is `Fresh`, and age greater than TTL is `Stale`. The calculator never reads a clock, and effective `IsReady` requires a valid complete formula and fresh inputs.
- Causal calculation filters every observation by inclusive event, availability, and stable-sequence bounds, and filters bars by inclusive `Bar.CloseTime <= cursor.AsOf`. Canonicalization is ordinal and permutation independent: exact duplicates count once, same-key conflicts and bar gaps produce stable quality reasons, late availability remains visible, and missing values are never filled or interpolated. Six T001 formulas and their golden behavior remain intact.
- TDD RED: the focused screener test command initially failed with the expected missing `ScreenerFreshnessPolicy` contract before T002 implementation. Focused `ScreenerFeatureCalculatorTests` passed 13/13 after implementation.
- `C:\Users\Администратор\.dotnet\dotnet.exe restore TradingBot.slnx --locked-mode` exited 0. `C:\Users\Администратор\.dotnet\dotnet.exe build TradingBot.slnx -c Release --warnaserror --no-restore` exited 0 with zero warnings and errors. Scoped format verification over `ScreenerFeatureContracts.cs` and `ScreenerFeatureCalculatorTests.cs` exited 0.
- `& .\tools\validate-docs.ps1` exited 0 with phases=19, milestones=41, tasks=119, ready=P05-M02-T003. `git diff --check` exited 0. Canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.

## P05-M02-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ScreenerSnapshotContractTests"` initially failed with the expected missing `ScreenerSnapshot` type before the Core implementation.
- `ScreenerSnapshot` normalizes causal input/value ordering and feature reasons through one canonical Core JSON path. It carries format version 1 separately from supported formula version `screener-v1`, preserves explicit nullable lookbacks and TTLs, and computes a SHA-256 identity without identity or current time in the payload. Reordered equivalent inputs receive the same identity; persisted input re-evaluates to the stored feature values.
- `SqliteDatabase` now migrates schema 6 -> 7 transactionally and validates the BLOB-keyed `screener_snapshots` table. `SqliteScreenerSnapshotStore.Append` is idempotent for exact retries, rejects corrupt/conflicting content and unsupported versions on read, commits before returning success, and rolls back under a held writer. Existing event, intent, reservation, operational, and capture rows remain readable after migration.
- Final focused verification passed screener fixtures 16/16 (including snapshot contracts 3/3), snapshot persistence fixtures 6/6, and affected existing Infrastructure persistence fixtures 38/38. Release warnings-as-errors builds for Core.Tests and Infrastructure.Tests passed with zero warnings/errors; locked restore, scoped format, docs validation, `git diff --check`, and canonical source SHA-256 passed. Full-suite and remote CI remain unverified.

## P05-M03-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~StagedSelectionTests"` initially failed with the expected missing `StagedSelectionKey` and `StagedSelectionConfiguration` contracts before production implementation.
- Core adds immutable format-one staged-selection configuration, input, result, candidate, exclusion, and comparison contracts. `UniverseSelectionStage` gates metadata by local availability at one caller-supplied cursor, applies `MetadataCandidateFilter`, collapses exact duplicate screener snapshot identities, rejects conflicting snapshots and future causal observations, recalculates from snapshot inputs, requires configured features to be complete and fresh, ranks by explicit Turnover/Spread lexical directions with ordinal symbol tie-break, and preserves metadata, snapshot evidence, recalculated values, comparisons, ranks, reasons, filter evidence, and cursor/configuration provenance. Capacity is bounded at 250 with no weights, defaults, TTLs, setup, order, or execution behavior.
- Focused staged fixtures passed 6/6, including availability/readiness, deterministic ranking and capacity, duplicate/conflict/no-lookahead behavior, culture/permutation stability, and canonical Core JSON round trip. Locked Core.Tests restore, Release warnings-as-errors build, scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M03-T002` was the next validator-confirmed READY task; P05-M01-T003 was blocked by P05-M03-T003 at that transition.

## P05-M03-T002 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CandidatePoolSelectionTests"` initially failed with the expected missing `CandidatePoolSelectionStage`, `PreviousStage`, and `StageIdentity` contracts before production implementation.
- Core adds `CandidatePoolSelectionStage`, an explicit format-one `StagedSelectionStageReference`, and replay-stable SHA-256 result identity. Candidate reduction accepts only the selected Universe membership and the Universe cursor, rejects new instruments, cursor/future/late/conflicting screener evidence, recalculates from saved inputs, gates only configured six-feature keys, and preserves previous metadata, evidence, comparisons, ranks, reasons, capacity exclusions, and prior-stage identity/membership. Missing optional feature inputs remain non-blocking when their keys are not configured.
- Six focused CandidatePool fixtures passed 6/6, covering prior-membership restriction and provenance, optional-feature readiness, conflict/future rejection, all six explicit keys, permutation/culture-independent results, capacity 50, and canonical JSON round trip. T001 staged fixtures passed 6/6; focused metadata/filter, screener, snapshot/serialization, architecture, and Core boundary fixtures passed; locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M03-T003` was the next validator-confirmed READY task; P05-M01-T003 was blocked by P05-M03-T003 at that transition.

## P05-M03-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealtimeMonitorSelectionTests"` initially failed with the expected missing `StagedSelectionChainSnapshot` contract before production implementation.
- Core adds `RealtimeMonitorSelectionStage`, which accepts only a CandidatePool result and same-cursor screener evidence. It enforces the CandidatePool membership, explicit six-feature configuration, complete/fresh readiness only for configured keys, deterministic lexical ranking, conflict/future handling, provenance, and capacity 20. The CandidatePool reference must point to Universe before monitor evaluation.
- `StagedSelectionChainSnapshot` stores the shared cursor, Universe input, per-stage screener evidence, all three configurations/results, stage exclusions/provenance, and format-one algorithm version. It normalizes canonical evidence order, validates Universe→CandidatePool→RealtimeMonitor identity/cursor/membership links, rebuilds all three stages on replay, and computes a lower-case SHA-256 identity excluded from canonical JSON. Altered links, skipped stages, unsupported format, and unsupported algorithm versions are rejected.
- Six RealtimeMonitor/chain fixtures passed 6/6, covering membership/provenance, configured-key readiness, conflict/future evidence, capacity 20, canonical round trip, same identity after replay, and altered/unsupported chain rejection. T001 and T002 staged fixtures passed 6/6 each; focused metadata/filter, screener, snapshot/serialization, architecture, and Core boundary fixtures passed; locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped C# format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. `P05-M01-T003` is the next validator-confirmed READY task; it was not implemented.

## P05-M01-T003 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Infrastructure.Tests\TradingBot.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteUniverseSelectionSnapshotStoreTests"` initially failed with the expected missing `SqliteUniverseSelectionSnapshotStore` type; fixture-only timestamp/identity issues were corrected before production implementation.
- `SqliteUniverseSelectionSnapshotStore` persists canonical `StagedSelectionChainSnapshot` JSON in the BLOB-keyed `universe_selection_snapshots` table. It exposes `Open`, `Append`, and `Find` by canonical hex or 32-byte SHA-256, returns false for exact retries, validates row format/algorithm metadata, content, canonical JSON, identity, and pure replay, and fails closed for corruption, conflicts, unsupported versions, altered chain content, non-canonical payloads, identity mismatches, and write-lock failures.
- `SqliteDatabase` now migrates schema 7 -> 8 transactionally and validates the exact four-column WITHOUT ROWID table. New databases and older schema fixtures reach version 8 while preserving prior event, screener, order, portfolio, operational, and capture rows. Existing schema-6 and schema-5 fixtures drop the test-only newer table before modeling the older version.
- The focused store fixtures passed 7/7. Related snapshot/migration/persistence fixtures passed 51/51: `SqliteScreenerSnapshotStoreTests` 6/6, `SqliteEventJournalTests` 9/9, `ReplayEnvelopeJournalTests` 2/2, `SqliteOrderIntentJournalTests` 6/6, `SqlitePortfolioReservationStoreTests` 11/11, `SqliteOperationalStateTests` 6/6, and `ParquetCaptureStoreTests` 4/4. Release Infrastructure.Tests build with warnings as errors passed with zero warnings/errors; locked restore, scoped format, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. No rolling indicators, setups, execution, or production mutation were added.
- Validator confirmed `ready=P06-M01-T001`; the rolling-indicator task is only READY and was not started.

## P06-M01-T001 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RollingIndicatorTests"` initially failed with the expected missing rolling-indicator contract types before production implementation.
- `RollingIndicatorCalculator` is a pure BCL-only Core calculator for ATR, SMA/EMA/WMA/RMA, and RVOL. The explicit `rolling-v1` configuration has no defaults, weights, thresholds, or TTLs. It filters event time, availability time, stable sequence, and closed-bar time at one `CausalEventCursor`, respects `HistoryStart`, calculates recursive seeds from the full available history, and retains canonical input, seed, quality, warmup, and exclusion provenance.
- Checked decimal arithmetic reports overflow and zero RVOL denominators explicitly. Exact duplicates collapse deterministically; conflicts and timeframe gaps block readiness without filling. Future or unavailable observations cannot alter an earlier cursor result. Strict Core JSON round trip, culture/permutation determinism, empty-input warmup, invalid configuration/input states, and Core immutability/boundary fixtures are covered.
- Focused rolling fixtures passed 9/9. Affected screener/serialization/boundary fixtures passed: `ScreenerFeatureCalculatorTests` 13/13, `ScreenerSnapshotContractTests` 3/3, `CoreJsonSerializationTests` 7/7, `ArchitectureTests` 1/1, and `CoreBoundaryTests` 2/2. Locked Core.Tests restore and Release Core.Tests build with warnings as errors passed with zero warnings/errors; scoped format verification, documentation validation, `git diff --check`, and canonical source SHA-256 verification passed. P06-M01-T002 is the next validator-confirmed READY task and was not implemented.

## P06-M01-T002 evidence

- TDD RED: `C:\Users\Администратор\.dotnet\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PriceContextFeatureTests"` initially failed at compile time with the expected missing `PriceVolumeObservation` Core contract before production implementation.
- `PriceContextFeatureCalculator` adds immutable `price-context-v1` Core contracts and pure calculations for one-bar floor pivots, rolling dynamic support/resistance, a configured Donchian-style channel, exact caller-supplied price-volume VWAP, explicitly binned POC, and configured SMA/EMA/WMA/RMA trend state. All lookbacks, POC bin size, moving-average kind, and version are caller supplied; missing or unsupported values remain feature-local unknown states.
- Causal selection applies inclusive event-time, availability-time, stable-sequence, and closed-bar bounds at one cursor. Canonical duplicate handling, conflicting/gapped input quality, late/future reasons, checked decimal overflow, zero-volume denominators, culture/permutation stability, and strict `CoreJsonSerializer` round trip are covered. OHLCV is never substituted for exact VWAP/POC source data.
- Focused PriceContextFeatureTests passed 7/7. Release Core.Tests build with warnings as errors passed with zero warnings/errors. Scoped format verification over `PriceContextFeatureContracts.cs` and `PriceContextFeatureTests.cs` passed. After the T002 row transition, `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `ready=P06-M01-T003`.

## P06-M01-T003 evidence

- `PriceContextReplayRegressionTests` adds eleven independent, hand-specified vectors for cursor-by-cursor batch/prefix replay parity, future-data no-lookahead, strict pivot confirmation timing, EMA/RMA close and HLC3 EMA/RMA centers, ATR and population-standard-deviation widths, frozen ATR break equality/wick/reversal, HTF distinct-bar confirmation and candidate/unknown reset including a pending Bear duplicate, late availability, duplicate/conflict handling, invalid/oversized/warmup/decimal-overflow configuration, exact population-SD sqrt rounding, permutation/culture determinism, and strict JSON round trip.
- Focused replay vectors passed 11/11; all PriceContext tests passed 27/27; RollingIndicator tests passed 9/9; Core JSON/boundary tests passed 9/9. Release Core.Tests build with `--warnaserror` passed with zero warnings/errors, scoped C# format verification passed, and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- `pwsh -NoProfile -File tools/validate-docs.ps1` passed with `phases=19, milestones=41, tasks=119, ready=P06-M02-T001` after the T003 row transition. `git diff --check` passed. No calculator or contract changes were needed for T003; no persistence, strategy, ranking, API, order, execution, or later Phase 6/7 behavior was added.

## P06 correction review and restart

- The earlier P06-M01-T002 evidence above records the original `price-context-v1` implementation and is superseded: review against canonical source §§7–10 and 13 found floor pivots, rolling extrema, a Donchian channel, and MA-cross trend instead of the specified confirmed structural pivots, ATR channel, dynamic trend rules, and separate HTF context. Invalid numeric periods and enum values also escaped as exceptions rather than feature-local unknown results.
- P06-M01-T002 was reopened as `IN_PROGRESS`, and the original v1 completion proof is superseded. Final review also found that an invalid unused ATR smoothing enum blocked a StandardDeviation channel; a red/green regression fixed the feature-local configuration dependency. Corrected T002 and the independent T003 replay regression are now `DONE`; P06-M02-T001 is now `READY`.
- Corrective work uses explicit configuration, strict full-window pivots with causal confirmation, nullable ATR evidence when a confirmation-time buffer cannot be calculated, frozen-buffer role reversal, caller-selected EMA/RMA smoothing for HLC3, ATR or population-standard-deviation channel width, local trend, and HTF confirmed context. Shared rolling math kernels preserve `rolling-v1` operation order. The source specification remains immutable.
- Final T002 verification: focused PriceContext, RollingIndicator, JSON serialization, Core boundary, and architecture tests passed 35/35; Core.Tests Release build with `--warnaserror` had 0 warnings/errors; scoped format, `git diff --check`, canonical source SHA-256, and `tools/validate-docs.ps1` passed. The validator reports `ready=P06-M01-T003`.
- T003 evidence above records the independent cursor-by-cursor replay, no-lookahead, strict pivot, late-data, duplicate/conflict, culture, serialization, pending-duplicate, reset, and deterministic population-SD vectors and their focused verification results. The validator confirms `P06-M02-T001` as the next READY task.

## P06-M02-T002 evidence

- TDD RED: focused Core snapshot compilation initially failed with the missing `FeatureQualitySnapshot` contract; focused Infrastructure store compilation initially failed with the missing `SqliteFeatureQualitySnapshotStore`, and the MeterListener fixture initially failed with the missing `FeatureQualityMetricsPublisher`.
- `FeatureQualitySnapshot` is format 1 with `feature-quality-v1`, retains complete rolling/price-context results, explicit policy, cursor, source formula versions, and quality, and computes a lower-case SHA-256 identity from canonical JSON excluding identity. `SqliteFeatureQualitySnapshotStore` provides Open, idempotent Append, and hex/32-byte Find; it validates schema metadata, versions, canonical JSON, identity, and evaluator recomputation.
- `SqliteDatabase` now migrates schema 8 -> 9 transactionally and validates the exact BLOB-keyed WITHOUT ROWID `feature_quality_snapshots` table. Existing older fixtures that retain the table are handled idempotently, while malformed definitions still fail exact validation. `FeatureQualityMetricsPublisher` emits readiness, stable reasons, market-data age, availability age, and known coverage with explicit unknown measurements.
- Focused Core quality/snapshot tests passed 29/29; focused feature-quality store/metrics tests passed 2/2; the relevant Infrastructure suite passed 65/65; locked restore and Release Infrastructure build with `--warnaserror` passed with zero warnings/errors. Persistence and feature-quality architecture notes were updated without changing the canonical source or future-phase behavior.
- `pwsh -NoProfile -File tools/validate-docs.ps1` is required immediately after this transition; the expected next validator-selected task is `P06-M02-T003`. No setup, execution, Phase 7, or Lane C behavior was added.

## P06-M02-T003 evidence

- TDD RED: `FeatureQualityRegressionTests.InvalidTtlIsPersistableAndRemainsUnknown` first failed because the evidence contract rejected a non-positive TTL instead of preserving the explicit invalid policy as Unknown. The minimal Core fix removed that rejection; the focused regression suite then passed 6/6.
- The independent synthetic Core fixture set covers TTL epsilon/equality/+epsilon, delayed old availability, absent and invalid policies, warmup, gaps, duplicates, conflicts, isolated rolling/setup/HTF/price-volume dependencies, causal future boundaries/no-lookahead, permutation/culture determinism, and strict JSON. Related FeatureQuality/Rolling/PriceContext checks passed 46/46.
- Infrastructure fixtures cover reopen/replay migration preservation, idempotent append, hex and 32-byte lookup, non-canonical and corrupt rows, unsupported format/algorithm metadata, recomputation mismatch, exact schema validation, held-writer rollback, and MeterListener readiness/reasons/both ages/explicit unknown coverage. Focused feature-quality store fixtures passed 4/4 and full Infrastructure tests passed 67/67.
- Release Core.Tests and Infrastructure.Tests builds with `--warnaserror` passed with zero warnings/errors; locked restore passed; scoped format verification passed. After the DONE transition, `pwsh -NoProfile -File tools/validate-docs.ps1` must pass with `P07-M01-T001` as first READY and `P13-M01-T001` also READY. The milestone boundary is complete; no Phase 7 or Lane C implementation was started.

## P07-M01-T001 evidence

- Startup protocol: worktree was clean at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T001,P13-M01-T001`. `P07-M01-T001` was the first READY task in document order and `P06-M02-T003` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BoundaryRejectionSetupTests"` failed before production code with the expected `CS0246` errors for missing `BoundaryRejectionSetupResult` and the other Boundary Rejection contracts.
- Core adds immutable `BoundaryRejectionSetupVersion`, explicit nullable `BoundaryRejectionSetupConfiguration`, `BoundaryRejectionConditionEvidence`, `BoundaryRejectionSetupObservation`, and `BoundaryRejectionSetupResult` contracts. `BoundaryRejectionSetupEvaluator.Evaluate` is pure and BCL-only; it consumes `price-context-v2` and `feature-quality-v1` at the same `CausalEventCursor`, checks the confirmed signal bar against the channel/local-trend/HTF evidence, and returns both directions.
- LONG/SHORT implement SETUP-01 exactly: confirmed HTF Bull/Bear, LocalTrend Bull/Bear, `Low <= Lower` plus strict `Close > Lower` (mirrored for short), causally eligible confirmed signal bar, caller-supplied positive minimum channel width against the full `Upper - Lower` span, and exactly one ready Channel/LocalTrend/HtfContext quality record matching formula, original state, and series. Unused VWAP/POC/pivot/level quality does not gate this family. Missing/invalid configuration, warmup, stale/incomplete/mismatched quality, and ineligible or inconsistent signal bars fail closed as explicit unknown evidence. Strict close-through is `Invalidated`; equality at a band is `NoSetup`.
- Focused GREEN: `BoundaryRejectionSetupTests` passed 11/11. Full `TradingBot.Core.Tests` passed 165/165. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` over the three new files exited 0.
- Documentation: [`docs/architecture/boundary-rejection.md`](docs/architecture/boundary-rejection.md) records the SETUP-01 mapping, full-span minimum-width interpretation, quality gate, states, invalidation, and causality. [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md) records the selective setup consumption. `docs/TASKS.md` marks T001 DONE and T002 READY; `HANDOFF.md` records this checkpoint.
- After the transition, the same bundled PowerShell validator exited 0 with `phases=19, milestones=41, tasks=119, ready=P07-M01-T002,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core`, `TradingBot.Core.Tests`, and the listed documentation. No lifecycle, persistence, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M01-T002` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M01-T002 evidence

- Startup protocol: worktree already contained the uncommitted, verified P07-M01-T001 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T002,P13-M01-T001`. `P07-M01-T002` was the first READY task in document order and `P07-M01-T001` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CenterRetestSetupTests"` failed before production code with the expected `CS0246` errors for missing `CenterRetestSetupResult` and the other Center Retest contracts.
- Core adds immutable `CenterRetestSetupVersion`, explicit nullable `CenterRetestSetupConfiguration`, `CenterRetestConditionEvidence`, `CenterRetestSetupObservation`, and `CenterRetestSetupResult` contracts. `CenterRetestSetupEvaluator.Evaluate` is pure and BCL-only; it consumes `price-context-v2` and `feature-quality-v1` at the same `CausalEventCursor`, checks the confirmed signal bar against the channel/local-trend/HTF evidence, and returns both directions.
- LONG/SHORT implement SETUP-02: confirmed HTF Bull/Bear, LocalTrend Bull/Bear (the canonical SHORT omission of `local bias` is explicitly mirrored as Bear), `Low <= Center` / `High >= Center`, `Open >= Center` / `Open <= Center`, `Close >= Center` / `Close <= Center`, and strict candle direction. The family uses only `PriceContextChannel.Center`; it has no minimum-width gate, width configuration, or channel-band dependency. Exactly one ready Channel/LocalTrend/HtfContext quality record matching formula, original state, and series is required; unused VWAP/POC/pivot/level quality does not gate this family. Missing/invalid configuration, warmup, stale/incomplete/mismatched quality, and ineligible or inconsistent signal bars fail closed as explicit unknown evidence. Strict close-through is `Invalidated`; equality at the center is `NoSetup`.
- Focused GREEN: `CenterRetestSetupTests` passed 12/12. Full `TradingBot.Core.Tests` passed 177/177. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` over the three new files exited 0.
- Documentation: [`docs/architecture/center-retest.md`](docs/architecture/center-retest.md) records the SETUP-02 mapping, symmetric SHORT local-bias interpretation, no-width-gate decision, quality gate, states, invalidation, and causality. [`docs/architecture/feature-quality.md`](docs/architecture/feature-quality.md) records both Lane A setup consumers. `docs/TASKS.md` marks T002 DONE and T003 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator exited 0 with `phases=19, milestones=41, tasks=119, ready=P07-M01-T003,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core`, `TradingBot.Core.Tests`, and the listed documentation. The T001 contracts, evaluator, tests, and architecture note were not modified. No lifecycle, persistence, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M01-T003` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M01-T003 evidence

- Startup protocol: worktree already contained the uncommitted, verified T001/T002 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M01-T003,P13-M01-T001`. `P07-M01-T003` was the first READY task in document order and `P07-M01-T002` was DONE.
- T003 is a test-only characterization/regression suite; no production contract was missing, so there is no compile-time RED. The first baseline exposed two fixture issues only: one short OHLC bar had `low > close`, and the `boundary-*-only` bars close through the Center, so Center Retest correctly reports `Invalidated` for them. Fixtures and expected states were corrected; no production code changed.
- Added `LaneASetupFixtures.cs` and `LaneASetupFixtureTests.cs`. The suite covers a 14-case hand-specified matrix (both families, both directions, Ready/NoSetup/Invalidated/UnknownQuality, and a zero-width Center case proving no width gate), evidence invariants, strict reason ordering, retained snapshot evidence, and V1/execution boundary guards.
- Replay evidence: every matrix case serializes/deserializes `PriceContextFeatureResult`, `FeatureQualityEvaluation`, and both setup results, re-evaluates both families, and compares results with direct evaluation. The real-history cursor-prefix replay covers `Time(1)` through `Time(30)` (10 cursors), serializes the snapshot at every cursor, and reproduces identical evidence; the final cursor is Ready Long for both families.
- No-lookahead/determinism: full-history and prefix calculations preserve historical feature values; future bars cannot make an earlier cursor Ready; reversed input order and `fr-FR`/`en-US` cultures do not change evidence. Strict JSON rejects unknown members.
- Focused GREEN: `LaneASetupFixtureTests` passed 6/6. Full `TradingBot.Core.Tests` passed 183/183. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New test files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` exited 0.
- Documentation: [`docs/architecture/lane-a-setup-fixtures.md`](docs/architecture/lane-a-setup-fixtures.md) records the matrix, serialized/cursor replay, no-lookahead, determinism, and V1 boundary. `docs/TASKS.md` marks T003 DONE and P07-M02-T001 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T001,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes are limited to `TradingBot.Core.Tests` and the listed documentation. No Core production contracts, evaluator, tests, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M02-T001` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M02-T001 evidence

- Startup protocol: worktree already contained the uncommitted, verified P07-M01-T001/T002/T003 changes at `5940f2a`; the bundled PowerShell 7.6.5 validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T001,P13-M01-T001`. `P07-M02-T001` was the first READY task in document order and `P07-M01-T003` was DONE.
- TDD RED: `C:\temp\dotnet10\dotnet.exe test tests\TradingBot.Core.Tests\TradingBot.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SetupEvidenceSnapshotTests"` failed before production code with the expected `CS0246` errors for missing `SetupEvidenceState`, `SetupEvidenceInputSequence`, `SetupEvidenceInvalidation`, and `SetupEvidenceSnapshot`.
- Core adds immutable `SetupEvidenceState`, `SetupEvidenceInputSequence`, `SetupEvidenceInvalidation`, and `SetupEvidenceSnapshot` contracts. The snapshot is per-direction (`setup-evidence-v1`, format 1), retains the complete `price-context-v2`/`feature-quality-v1` input, exactly one Boundary/Center configuration/observation, a validated causal sequence (cursor, source, instrument, setup/HTF timeframes and history starts, signal bar, family/price-context/quality versions), and explicit invalidation state/ClosedThrough/reasons.
- Constructor validation enforces exactly one family payload, shared cursors, causal-sequence/input agreement, configuration/observation consistency, invalidation consistency with the selected observation, and a fresh family evaluation that must reproduce the observation. `SnapshotId` is the lower-case SHA-256 of canonical JSON excluding identity; `Replay()` re-evaluates the family and reproduces the identity. Tampered direction/setup type/invalidation/cursor/condition payloads are rejected during JSON deserialization.
- Focused GREEN: `SetupEvidenceSnapshotTests` passed 7/7, covering both families and directions, Ready/NoSetup/Invalidated/UnknownQuality, unknown evidence without a signal bar, canonical/direction-specific identity, JSON round-trip and tamper rejection, replay, and no lifecycle/decision/risk/order contracts. Full `TradingBot.Core.Tests` passed 190/190. Locked restore of `TradingBot.Core.Tests` exited 0; Release build with `--warnaserror` exited 0 with zero warnings/errors. New C# files were normalized to UTF-8 BOM + CRLF, and scoped `dotnet format --verify-no-changes` exited 0.
- Documentation: [`docs/architecture/setup-evidence.md`](docs/architecture/setup-evidence.md) records the schema, causal sequence, invalidation, identity/replay, and boundaries. `docs/TASKS.md` marks T001 DONE and P07-M02-T002 READY; this checkpoint is recorded in `HANDOFF.md`.
- After the transition, the same bundled PowerShell validator reported `phases=19, milestones=41, tasks=119, ready=P07-M02-T002,P13-M01-T001`; `git diff --check` exited 0; canonical `docs/sources/FINAL_TECH_SPEC.md` SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope: changes add `TradingBot.Core` evidence contracts/snapshot, focused Core tests, and the listed documentation. Existing T001/T002/T003 contracts, evaluators, tests, and architecture notes were not modified. No persistence, SQLite schema, lifecycle, decision, risk, order, execution, package, project-graph, or canonical-source change was made. No commit or push was performed. `P07-M02-T002` is only READY and was not started; `P13-M01-T001` remains READY and was not started; blocked tasks were not touched. Remote CI remains unverified.

## P07-M02-T002 evidence

- Startup: clean worktree at `94c1331`; P07-M02-T001 was DONE, and P07-M02-T002 was the first READY task; P13-M01-T001 was also READY.
- Added `LifecycleInputFixtures.cs` and `LifecycleInputFixtureTests.cs` under Core.Tests. The hand-specified 17-case matrix covers Boundary Rejection and Center Retest, both directions, Ready/NoSetup/Invalidated/UnknownQuality and warmup without a signal bar. Expected states are independent of the observed evidence state.
- Tests replay and round-trip each evidence snapshot, checking equality, stable identity, causal cursor/source/instrument, sorted invalidation reasons and signal-bar presence. Cursor-prefix replay verifies that earlier evidence stays non-ready despite future history, while fixture evidence demonstrates later Ready; repeated evidence replay is stable without modeling lifecycle deduplication.
- Reflection recursively inspects evidence properties, collection element types and method return types for lifecycle/Decision/RiskDecision/OrderIntent/Position surfaces; strict JSON rejects injected `orderIntent` and `authorized` fields. This proves the evidence contract boundary and does not instantiate an executor or lifecycle state machine.
- Focused Release Core.Tests (`LifecycleInputFixtureTests|SetupEvidenceSnapshotTests|LaneASetupFixtureTests`, `TreatWarningsAsErrors=true`) passed 18/18; scoped format passed; `tools/validate-docs.ps1` passed with `ready=P08-M01-T001,P13-M01-T001`; `git diff --check` passed; canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Scope is limited to Core.Tests fixtures/tests and TASKS/HANDOFF docs. No Core production code, lifecycle, persistence, decision, risk, order, or execution behavior changed. Remote CI remains unverified.

## P08-M01-T001/T002/T003 evidence

- Startup: the bundled validator selected `P08-M01-T001` as the first READY task after `P07-M02-T002`; `P13-M01-T001` remained independently READY. Existing P07 changes were preserved. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused lifecycle test compile initially failed with missing `SetupLifecycleConfiguration`, `SetupLifecycleIdentity`, `SetupLifecycleInput`, `SetupLifecycleSnapshot`, and related engine contracts. Core then adds BCL-only immutable lifecycle contracts and a pure `SetupLifecycleEngine` over explicit external facts. Inputs retain event/receive/availability times, stable sequence, stream/event identity, evidence, confirmation, external plan/order/position/exit identifiers, provenance, and rules/config versions. Identity fingerprints include instrument, setup family, direction, structural anchor, anchor time, and context version.
- T001 lifecycle evidence: legal transitions cover context, structure, setup evidence, confirmation, external plan, order/position/exit observations, cooldown, terminal, explicit reset, and recovery. Ready evidence advances one stage only; unknown evidence and invalid transitions fail closed. `SetupLifecycleTransition` records previous/current state and identity, canonical full input JSON, provenance, rules/config versions, reasons, and opaque shadow/outcome observability. `InstrumentLifecycleProcessor` serializes complete read-modify-write per instrument key, permits different instruments to proceed independently, and leaves the previous snapshot visible after a rejected commit.
- T002 Decision Engine evidence: `DecisionEngineConfiguration`, `DecisionEngineInput`, `DecisionEvidence`, and `DecisionEngineResult` define an explicit Allow/Shadow profile. Lane A must be Ready and visible at the lifecycle cursor; veto, unknown, stale, future, or conflicting evidence blocks; required Lane B/C evidence must confirm unless an explicit neutral flag permits it; only `EntryArmed` without recovery is eligible for Allow or Shadow. The result contains the existing `Decision` plus the immutable input snapshot and has no RiskDecision, OrderIntent, Position, sizing, or execution surface.
- T003 idempotency/recovery evidence: exact stream/event identity repeats return `Duplicate` without a new receipt; conflicting content returns `IdentityConflict`, suspends the snapshot, and requires explicit recovery; late inputs return `Late` without state/cursor rollback; `Replay` reproduces the ordered transition list; recovery accepts a serialized snapshot; and commit rejection leaves the prior state. Focused `SetupLifecycleTests` passed 12/12, the combined lifecycle/evidence fixture filter passed 30/30, full Core.Tests passed 207/207, Core Release build with `--warnaserror` passed with zero warnings/errors, scoped format passed, and strict JSON round-trip/unknown-field, duplicate/conflict/late, same-instrument barrier, separate-instrument, Decision rule-table, and recovery fixtures passed.
- Architecture notes are [`docs/architecture/setup-lifecycle.md`](docs/architecture/setup-lifecycle.md) and [`docs/architecture/decision-engine.md`](docs/architecture/decision-engine.md). Scope is limited to Core lifecycle/decision contracts, Core tests, architecture notes, TASKS, and HANDOFF. No SQLite persistence, P08-M02 projections/reason codes, replay runner, Risk Engine, order execution, P09, P13, package graph, or canonical source changes were made. No commit or push was performed; remote CI remains unverified.

## P08-M02-T001 evidence

- Startup: the bundled validator selected `P08-M02-T001` as the first READY task; `P08-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused Core and Infrastructure projection tests initially failed to compile because `DecisionProjectionSnapshot` and `SqliteDecisionProjectionStore` were absent. GREEN adds the immutable Core projection and the SQLite store.
- Projection evidence: format-one `DecisionProjectionSnapshot` retains the complete Decision Engine result, both lifecycle/decision configurations, explicit `decision-reasons-v1`, and a lifecycle checkpoint exposing identity, causal cursor, availability time, receipts, latest evidence, diagnostics, and recovery state. Canonical lower-case SHA-256 identity excludes the identity property. Construction and read-back re-evaluate the saved decision and reject semantic drift.
- Persistence evidence: `SqliteDecisionProjectionStore.Open/Append/Find` uses a BLOB-keyed `decision_projections` table, idempotent exact retry, conflicting identity and DecisionId rejection, canonical JSON/row metadata/identity validation, and schema 9 -> 10 migration. Existing event journal rows survive migration. ALLOW/BLOCK/SHADOW append/find/reopen fixtures passed 3/3; the affected Infrastructure migration/persistence filter passed 61/61; Core projection fixtures passed 4/4.
- Validation: Release tests with warnings as errors and scoped `dotnet format --verify-no-changes` passed for Core, Infrastructure, and the focused test files. `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. No P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed. `P08-M02-T002` is now the first READY task and `P13-M01-T001` remains READY.

## P08-M02-T002 evidence

- Startup: the bundled validator selected `P08-M02-T002` as the first READY task; `P08-M02-T001` was DONE and `P13-M01-T001` remained READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused reason-code contract test failed to compile because `DecisionReasonCodes` and `DecisionReasonCodeDefinition` were absent. GREEN adds the immutable, serializable catalog and switches Decision Engine emission to catalog constants/helpers without changing literal values, order, or duplicates.
- Contract evidence: `decision-reasons-v1` exposes stable constants, ordered `Codes`, auditable `Definitions`, JSON round-trip, lane mapping helpers, `RequireVersion`, and `RequireKnown`. Projection construction now rejects unknown reason versions and codes; persisted reads retain the same fail-closed behavior.
- Validation: focused reason/projection/lifecycle Core tests passed 20/20 with Release warnings-as-errors; projection store tests passed 6/6. Scoped format verification passed; `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. `P08-M02-T003` is now READY and `P13-M01-T001` remains READY. No replay runner, P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed.

## P08-M02-T003 evidence

- Startup: the bundled validator selected `P08-M02-T003` as the first READY task; `P08-M02-T002` was DONE and `P13-M01-T001` remained READY. The canonical source hash remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Recovery fixture evidence: `LifecycleRecoveryFixtures` builds an equal-timestamp causal prefix, saves its complete lifecycle checkpoint in a decision projection, closes/reopens SQLite, then feeds duplicate, late/out-of-order, identity-conflict, and explicit Recovery inputs to both branches. The continuous `SetupLifecycleEngine.Replay` branch receives the identical sequence; every tail transition status/canonical JSON and per-step Decision Engine JSON/reasons match, and final identity/state/cursor/availability/receipts/diagnostics/recovery state are equal.
- Failure evidence: a held SQLite writer rejects the checkpoint append with `SqliteException`, leaves no partial projection row, and allows the same append after rollback. No second replay runner or event reordering was added.
- Validation: focused Recovery + projection Release tests with warnings-as-errors passed 8/8; scoped format verification passed; `tools/validate-docs.ps1` and `git diff --check` passed after the DONE transition. `P09-M01-T001` and `P13-M01-T001` are now READY. No P09/P13 implementation, package, project-graph, or canonical-source change was made; no commit or push was performed.

## P09-M01-T001 evidence

- Startup: the validator-selected first READY task was `P09-M01-T001`; `P08-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build first failed with missing `ReplayRunner`, `HistoricalReplayRunner`, `ReplayRunResult`, and diagnostic contracts. GREEN adds `ReplayRunner` and immutable context/result/diagnostic types in Backtesting plus `HistoricalReplayRunner` one-shot forwarding.
- Runner evidence: one stream is validated before delivery; exact duplicate envelopes are skipped with diagnostics; identity and stable-sequence conflicts stop the run. The virtual clock is `max(EventTime, AvailabilityTime, Bar.CloseTime)`, with event time and stable sequence tie ordering. Each handler receives an existing cursor-filtered immutable prefix, preserving receive/exchange metadata. A handler exception commits no failing step and prevents later steps.
- Focused GREEN: `HistoricalReplayRunnerTests` passed 4/4; Release Backtesting/Replay build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed. The affected Replay.Tests assembly filter returned four passing tests.
- Documentation: [`docs/architecture/event-model.md`](docs/architecture/event-model.md) records runner availability and failure semantics. `docs/TASKS.md` marks `P09-M01-T001` DONE and `P09-M01-T002` READY.
- Scope: changes are limited to Backtesting, Replay.Tests, the event-model architecture note, TASKS, and this handoff. No Core contract, execution, package, project-graph, or canonical-source change was made; no commit or push was performed. `P09-M01-T002` is now the first READY task and will be rechecked before implementation.

## P09-M01-T002 evidence

- Startup: after T001, the validator reported `P09-M01-T002` as the first READY task with `P09-M01-T001` DONE; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with missing `SimulatedRiskPlan`, `SimulatedTakeProfit`, and `SimulatedFillAssumptions`. GREEN adds the injected `SimulatedRiskSimulator`, immutable plan/assumption/fill/result contracts, and the `BacktestRiskSimulator` forwarding alias.
- Simulation evidence: `IRiskMathCalculator.Calculate` is called once and its versioned result supplies quantity and protective stop. Entry uses the first bar opened after the decision; same-bar exits are excluded. TP1/TP2 fractions, next-bar breakeven, explicit breakeven stop pricing with no entry-price fallback, SL-first same-bar ambiguity, adverse stop-gap opens, per-fill fee/slippage, time exit, end-of-data open remainder, and checked decimal overflow are covered. The plan now carries an explicit Core `Decision` link and fill assumptions carry an explicit `FillSimulationVersion`; missing links/versions block simulation. No order, authorization, or execution adapter surface is exposed.
- Focused GREEN: the final combined HistoricalReplayRunner/SimulatedRisk filter passed 10/10, including explicit-link/version hardening; Release Backtesting/Replay build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed.
- Documentation: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the injected risk boundary, explicit assumptions, and research-only results. `docs/TASKS.md` marks `P09-M01-T002` DONE and `P09-M01-T003` READY.
- Scope: changes are limited to Backtesting, Replay.Tests, risk-model/event-model architecture notes, TASKS, and this handoff. No Core contract, persistence, package, project-graph, or canonical-source change was made; no commit or push was performed. `P09-M01-T003` is now the first READY task and will be rechecked before implementation.

## P09-M01-T003 evidence

- Startup: after T002, the validator reported `P09-M01-T003` as the first READY task with `P09-M01-T002` DONE; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with 14 expected missing report, comparison, limitation, configuration, and atomic-journal symbols. GREEN adds the versioned `BacktestReport`, deterministic `BacktestRegression`, explicit OHLCV limitation catalog, and temporary-file/flush/replace `BacktestReportJournal`.
- Regression evidence: reports retain causal event fields and Core payload JSON, setup identity/lifecycle transition references, Decision IDs/outcomes/reasons, injected risk results, simulated fills, costs, diagnostics, exit reason, and open remainder. Fingerprints exclude only RunId, so repeated runs compare equal across input permutations and Core JSON serialized/deserialized inputs while full canonical JSON remains auditable.
- Limitations evidence: every report states that OHLCV cannot reconstruct intrabar sequence or prove actual fills, spread, queue position, L2 state, or true point of control; TP portions remain management assumptions and liquidity/exchange partial fills are unproven. The report has no authorization, order, or execution surface.
- Focused GREEN: all scoped `TradingBot.Replay.Tests` Release tests passed 19/19; Backtesting/Replay Release build with `--warnaserror` passed with zero warnings/errors; scoped format verification and `git diff --check` passed.
- Documentation: [`docs/architecture/backtest-report.md`](docs/architecture/backtest-report.md) records the canonical report, comparison fingerprint, atomic export, retained causal references, and OHLCV evidence limits. `docs/TASKS.md` marks `P09-M01-T003` DONE.
- Scope: changes are limited to Backtesting, Replay.Tests, the report architecture note, TASKS, and this handoff. No Core contract, project graph, package, persistence, execution, P09-M02/P13-M01 work, or canonical-source change was made; no commit or push was performed. After validation, `P09-M02-T001` and `P13-M01-T001` are the expected READY tasks; implementation stopped at P09-M01.

## P09-M02-T001 evidence

- Startup: PowerShell 7 validator selected `P09-M02-T001` as the first READY task; `P09-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `LinearPerpetualRiskMathCalculator` type. The focused Replay.Tests parity build then failed its tampered-input assertion because the simulator checked only instrument, direction, and version.
- GREEN: Core adds BCL-only `LinearPerpetualRiskMathCalculator` version `linear-perpetual-risk-v1`. It uses checked decimal `riskBudget = equity * fraction`, `lossPerUnit = abs(entry - stop) + fee + slippage`, `quantity = budget / loss`, and `notional = entry * quantity`, with no venue rounding and explicit overflow/zero-precision rejection. `SimulatedRiskSimulator` now requires the complete returned `RiskMathInput` to equal the plan input and requires the injected calculator version.
- Evidence: independent LONG/SHORT vectors, high-precision decimal JSON round-trip, matching currency/stop guards, overflow/zero-precision behavior, direct planning calculation, replay simulation parity, and tampered full-input rejection passed. Core calculator tests passed 4/4; combined Replay parity and existing simulation tests passed 8/8. Release builds with warnings-as-errors passed with zero warnings/errors. Scoped include-only format checks and `git diff --check` passed; the PowerShell 7 docs validator passed with `ready=P09-M02-T002,P13-M01-T001`.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the formula, fixed version, decimal/venue boundary, parity evidence, and the fact that calculation parity does not establish profitability or replace the Risk Engine. `docs/TASKS.md` marks `P09-M02-T001` DONE and `P09-M02-T002` READY.
- Scope: changes are limited to the Core calculator, Backtesting input/version validation, focused Core/Replay tests, risk-model architecture note, TASKS, and this handoff. No fill simulator, execution authorization, package, project-graph, persistence, P09-M03/P13-M01 behavior, or canonical source change was made; no commit or push was performed.

## P09-M02-T002 evidence

- Startup: after T001, the PowerShell 7 validator selected `P09-M02-T002` as the first READY task; `P09-M02-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with the expected missing two-argument `SimulatedRiskSimulator` constructor and missing `OhlcvFillSimulator` type.
- GREEN: `SimulatedRiskSimulator` retains its calculator-only constructor and adds an `IFillSimulator` overload. The default `OhlcvFillSimulator` (`ohlcv-fill-v1`) returns one reference-price observation per causal request. The harness retains trigger selection, TP/time sizing, numeric fee/slippage accounting, role assignment, causal event mapping, and research-only result construction. It rejects model result/input/version mismatches, overfills, future observations, and stable sequences outside the visible prefix. `SimulatedRiskSimulationResult` records the injected fill-model version.
- Evidence: replaceable model output changes the fill price while the `RiskMathResult` remains byte-for-byte equal to direct planning; the captured input excludes a future bar; invalid future observations fail closed; the public model surface contains no risk, order, authorization, or execution names. Focused isolation, existing simulation, and parity tests passed 11/11. Release Backtesting/Replay build with warnings-as-errors passed with zero warnings/errors; include-only format checks and `git diff --check` passed.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) records the model seam, causal-prefix validation, numeric-cost ownership, and research-only boundary. `docs/TASKS.md` marks `P09-M02-T002` DONE and `P09-M02-T003` READY.
- Scope: changes are limited to Backtesting simulation/fill injection, focused Replay tests, risk-model architecture note, TASKS, and this handoff. No execution authorization, persistence, package, project-graph, P09-M03/P13-M01 behavior, or canonical source change was made; no commit or push was performed.

## P09-M02-T003 evidence

- Startup: after T002, the PowerShell 7 validator selected `P09-M02-T003` as the first READY task; `P09-M02-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused BacktestRegressionTests build failed because `BacktestReport` did not expose the injected `FillSimulationVersion` or serialize that model identity.
- GREEN: `BacktestReport` now retains the fill-model version beside the shared `RiskMathResult`, full `RiskPlan.FillAssumptions`, simulated costs, fills, causal references, diagnostics, and open remainder. Canonical serialization and the run-identity-independent fingerprint include the model version and assumptions. Existing atomic UTF-8 report replacement remains unchanged.
- Evidence: the report fixture proves the default `ohlcv-fill-v1` model and caller-supplied assumption version are both present in canonical JSON; five focused report tests passed. The complete affected Replay.Tests Release run passed 25/25; Backtesting/Replay build with warnings-as-errors passed with zero warnings/errors; include-only format checks, `git diff --check`, and the PowerShell 7 docs validator passed.
- Limitations: the report states that OHLCV cannot recover intrabar event sequence, actual fills, spread, queue position, Level 2, or true point of control. TP portions remain management assumptions; liquidity and exchange partial fills are unproven. Reproducibility and parity are engineering evidence only and do not establish profitability or live execution quality.
- Architecture: [`docs/architecture/risk-model.md`](docs/architecture/risk-model.md) and [`docs/architecture/backtest-report.md`](docs/architecture/backtest-report.md) record the version/assumption links, deterministic fingerprint, and evidence limits. `docs/TASKS.md` marks `P09-M02-T003` DONE.
- Scope: changes are limited to the Core calculator, Backtesting simulator/report, focused Core/Replay tests, risk/backtest architecture notes, TASKS, and this handoff. No P09-M03/P13-M01 implementation, execution authorization, persistence, package, project-graph, or canonical source change was made; no commit or push was performed.

## P09-M03-T001 evidence

- Startup: the PowerShell validator selected `P09-M03-T001` as the first READY task; `P09-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD: the focused Replay.Tests build first failed with the expected missing `FrozenResearchManifest` type. GREEN adds the BCL-only immutable manifest, canonical causal event/configuration hashes, strict integrity restore, pre-run replay validation, write-once manifest journal, and immutable evaluation snapshot linked to the canonical report.
- Evidence: `ResearchFreezeTests` passed 4/4; the complete affected Replay.Tests assembly passed 29/29. Backtesting Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed. Exact duplicate causal rows remain in the frozen manifest while the existing replay runner delivers them once by its established idempotent semantics.
- Architecture: [`docs/architecture/research-freeze.md`](docs/architecture/research-freeze.md) records the frozen fields, canonical ordering, integrity checks, pre-run binding, write-once journal, snapshot capture, and evidence limits. The fixtures are synthetic and do not establish real OOS provenance or profitability.
- Scope: changes are limited to Backtesting, Replay.Tests, the research-freeze architecture note, TASKS, and this handoff. No Core contracts, Infrastructure dependency, package, project graph, execution behavior, P13 behavior, or canonical source change was made; no commit or push was performed. `P09-M03-T002` is now the first READY task and must be rechecked before implementation.

## P09-M03-T002 evidence

- Startup: after T001, the PowerShell validator selected `P09-M03-T002` as the first READY task; `P09-M03-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD: the focused criteria build first failed with the expected missing `PromotionCriterion`, `PredeclaredPromotionCriteria`, journal, and evaluation-context types. GREEN adds immutable manifest-bound criteria with stable IDs/questions/evidence, explicit unresolved blockers, strict canonical restore, a write-once journal, and a context that blocks replay/capture until matching criteria are registered and ready.
- Evidence: `PromotionCriteriaTests` passed 3/3; the complete affected Replay.Tests assembly passed 32/32. Release Backtesting and Replay builds with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed. No automatic promotion decision or numeric threshold was added.
- Architecture: [`docs/architecture/research-freeze.md`](docs/architecture/research-freeze.md) records the criteria identity, manifest binding, unresolved-blocker readiness gate, write-once registration, and evidence boundary.
- Scope: changes are limited to Backtesting, Replay.Tests, the research-freeze architecture note, TASKS, and this handoff. No Core contracts, Infrastructure dependency, package, project graph, execution behavior, P13 behavior, or canonical source change was made; no commit or push was performed. `P10-M01-T001` is now the first READY task; `P13-M01-T001` remains independently READY and implementation stopped at P09-M03.

## P10-M01-T001 evidence

- Startup: the validator selected `P10-M01-T001` as the first READY task; `P09-M03-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build first failed with the expected missing `LaneBOpenInterestObservation` and `LaneBFundingObservation` contracts.
- GREEN: Core adds immutable `LaneBInput`, causal OI/funding observations that wrap the existing `OIUpdate`/`FundingUpdate`, explicit source/event identity, instrument, units, funding kind, provenance, event/receive/availability/exchange times, and stable sequence. `LaneBInput.Select` filters the shared causal prefix, preserves exact decimal values, computes only explicitly configured absolute OI deltas, and reports unknown configuration, missing, unavailable, incompatible, conflict, insufficient-history, duplicate, and overflow states without defaults.
- Focused GREEN: `LaneBInputTests` passed 4/4. The Core Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records the causal input boundary, explicit unit/configuration rules, duplicate/conflict behavior, and synthetic-fixture limits.
- Scope: changes are limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No provider/Binance mapping, Decision handoff, persistence, package, project-graph, or canonical-source change was made; `P10-M01-T002` is now the first READY task and must be rechecked before implementation.

## P10-M01-T002 evidence

- Startup: after T001 the validator selected `P10-M01-T002` as the first READY task; `P10-M01-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build first failed with the expected missing liquidation, failed-continuation, rule-table, evaluator, and shadow-result contracts.
- GREEN: Core adds standalone causal `LaneBLiquidationObservation` and externally supplied `LaneBFailedContinuationObservation` with method versions, outcomes, causal links, and provenance. `LaneBEvaluator` consumes only a caller-declared, setup/direction-specific metric table and returns `lane-b-shadow-v1` outcomes (`Confirm`, `Neutral`, `Veto`, `InsufficientData`) with reasons and used IDs. Missing liquidation coverage, unknown failed-continuation methods/links, missing rules, and OI-only confirmation remain insufficient; the evaluator has no Decision, RiskDecision, OrderIntent, or authorization surface.
- Focused GREEN: `LaneBShadowTests` passed 4/4. The Core Release build with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records standalone liquidation/behavior evidence, explicit rule-table metrics, shadow statuses, external failed-continuation limits, and the absence of a Decision handoff.
- Scope: changes remain limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No Binance mapping, provider integration, persistence, package, project-graph, Decision handoff, or canonical-source change was made; `P10-M01-T003` is now the first READY task and must be rechecked before implementation.

## P10-M01-T003 evidence

- Startup: after T002 the validator selected `P10-M01-T003` as the first READY task; `P10-M01-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused freshness/replay build first failed with the expected missing TTL policy, freshness evidence/evaluator, and Lane B replay fixture contracts.
- GREEN: Core adds caller-supplied per-series TTL policy with unknown/non-positive values blocked, independent event and availability ages with equality-at-TTL freshness, and `LaneBEvaluator` policy gating. Exact duplicates are retained once with diagnostics; same-identity conflicts block affected evidence; input units/sources and liquidation arithmetic remain explicit failures. Existing `HistoricalReplayRunner` feeds causal OI/funding prefixes to the same selection/evaluator, preserving late records and prior cursor results.
- Focused GREEN: `LaneBInputTests`, `LaneBShadowTests`, and `LaneBFreshnessTests` passed 13/13; `LaneBReplayTests` passed 4/4. Core and Replay Release builds with warnings-as-errors passed with zero warnings/errors; scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Architecture: [`docs/architecture/lane-b.md`](docs/architecture/lane-b.md) records the caller TTL boundary, independent ages, no-lookahead replay evidence, duplicate/conflict handling, and freshness limitations.
- Scope: changes are limited to Core, Core.Tests, Replay.Tests, the Lane B architecture note, TASKS, and this handoff. No provider/Binance mapping, persistent causal join, Decision handoff, package, project-graph, or canonical-source change was made. After validation the expected READY tasks are `P10-M02-T001` and `P13-M01-T001`; implementation stops at P10-M01.

## P10-M02-T001 evidence

- Startup: after P10-M01-T003, the PowerShell 7 validator selected `P10-M02-T001` as the first READY task; `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `LaneBQualityState`, `LaneBQualityStatus`, and `LaneBQualityEvaluator` symbols. No production implementation existed before the test.
- GREEN: Core adds immutable `LaneBQualityState` format 1 / `lane-b-quality-v1` and a pure evaluator over the existing `LaneBShadowResult`. It retains the causal cursor, sorted observation identities, caller-owned freshness evidence, and stable reasons. Missing, stale, unavailable, conflict, incompatible, arithmetic-overflow, and unknown states stay distinct; a missing freshness policy is unknown and cannot produce ready quality.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Core.Tests.LaneBQualityTests` passed 5/5. The fixtures cover absent policy, missing/unavailable, stale/conflict/overflow, cursor incompatibility, strict JSON round trip, and unknown-field rejection.
- Verification: `C:\temp\dotnet10\dotnet.exe build tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj -c Release --no-restore --warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed; `tools/validate-docs.ps1` passed; `git diff --check` passed.
- Scope: changes are limited to Core, Core.Tests, the Lane B architecture note, TASKS, and this handoff. No provider mapping, persistence, replay-runner, Decision handoff, package, project-graph, P13, or canonical-source change was made. `P10-M02-T002` is now the first READY task and must be rechecked before implementation.

## P10-M02-T002 evidence

- Startup: after P10-M02-T001, the PowerShell 7 validator selected `P10-M02-T002` as the first READY task; `P13-M01-T001` remained independently READY. T001 was DONE and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Replay.Tests build failed with the expected missing `LaneBJoinSnapshot` symbol. No join implementation existed before the test.
- GREEN: Core adds immutable `LaneBObservationReference` and `LaneBJoinSnapshot` (`lane-b-join-v1`, format 1). The join filters event/availability/stable-sequence eligibility before deterministic ordering, preserves event/exchange/receive/availability/source/provenance/stable sequence and canonical payload identity, collapses exact duplicates, and retains conflicting same-identity rows while changing quality to `Conflict`. The existing `HistoricalReplayRunner` is unchanged.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Replay.Tests.LaneBJoinTests` passed 6/6. Fixtures cover permutations, equal timestamps, exact duplicates, conflicts, future/delayed prefix filtering, runner integration, and strict JSON round trip.
- Verification: Core and Replay Release builds with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed; `tools/validate-docs.ps1` passed; `git diff --check` passed.
- Scope: changes are limited to Core, Replay.Tests, the Lane B architecture note, TASKS, and this handoff. No MarketEvent or replay-runner change, provider mapping, persistence, Decision handoff, package, project-graph, P13, or canonical-source change was made. `P10-M02-T003` is now the first READY task and must be rechecked before implementation.

## P10-M02-T003 evidence

- Startup: after P10-M02-T002, the PowerShell 7 validator selected `P10-M02-T003` as the first READY task; `P13-M01-T001` remained independently READY. T001 and T002 were DONE and the canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused Core.Tests build failed with the expected missing `DecisionEvidence.FromLaneBJoin`, `LaneBDecisionEvidenceMetadata`, optional metadata property, and seven-argument constructor symbols. No handoff implementation existed before the test.
- GREEN: Core adds typed optional `LaneBDecisionEvidenceMetadata` (`lane-b-decision-evidence-v1`, format 1) and validated `DecisionEvidence.FromLaneBJoin`. Metadata retains the causal join identity, shadow evidence identity/version/status, quality status/reasons, cursor, and observation references. Unready quality maps to existing `DecisionEvidenceStatus.Unknown`; cursor, identity, algorithm-version, lane, and status mismatches fail closed. Null metadata is omitted from JSON so legacy projection canonical JSON and identities remain unchanged.
- Focused GREEN: direct xUnit DLL execution of `TradingBot.Core.Tests.DecisionEvidenceMetadataTests` passed 5/5. Fixtures cover ready/missing/stale/conflict visibility, strict metadata round trip, inconsistent metadata rejection, lifecycle/recovery blocking, deterministic traces, and absence of risk/order/authorization surfaces.
- Adjacent regression evidence: direct xUnit DLL execution passed `DecisionProjectionSnapshotTests` 4/4, `SetupLifecycleTests` 12/12, and existing `LaneBReplayTests` 4/4. Core and Replay Release builds with `--warnaserror` passed with zero warnings/errors; scoped `dotnet format --verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed. One concurrent Core/Replay build attempt hit a shared-output file lock; the required sequential Core rebuild and Replay build both passed.
- Scope: changes are limited to Core, Core.Tests, Replay.Tests, Lane B/Decision architecture notes, TASKS, and this handoff. No RiskDecision, OrderIntent, authorization, MarketEvent, replay-runner, provider, persistence, package, project-graph, P13, or canonical-source change was made. `P11-M01-T001` and `P13-M01-T001` are now the expected READY tasks; implementation stops at P10-M02.

## P11-M02-T001 evidence

- Startup: the PowerShell 7 validator selected `P11-M02-T001` as the first READY task; `P11-M01-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: the focused risk-reservation build first failed because the unauthenticated two-argument reservation API and risk-evidence read API were absent. A compile-only shell then produced 5/5 focused runtime failures from the unimplemented methods, with the test class discovered and executing.
- GREEN: `SqlitePortfolioReservationStore.TryReserve` now requires the complete `RiskEvaluationResult`, expected reconciliation revision, and shared `IRiskMathCalculator`; it recomputes and canonically compares evidence, requires approval and matching setup/portfolio/instrument/direction/quantity/risk/exposure/slot/configuration identities, rereads totals under one immediate transaction, and atomically stores intent, reservation, and risk evidence. Repeated identical calls return `AlreadyReserved`; conflicts, stale/unresolved acknowledgement, missing portfolios, capacity overflow, and calculator/evidence failures leave no partial rows. Legacy reservations remain counted and cannot be silently reauthorized. Schema 10 -> 11 adds the durable `risk_evaluations` table and restart read API.
- Focused GREEN: `SqliteRiskReservationTests` passed 6/6, `SqlitePortfolioReservationStoreTests` passed 11/11, and `SqliteOperationalStateTests` passed 6/6. The Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors.
- Documentation: `docs/architecture/persistence.md`, `docs/architecture/execution-model.md`, and `docs/architecture/risk-model.md` record the authenticated reservation boundary and schema migration. `docs/TASKS.md` marks `P11-M02-T001` DONE and `P11-M02-T002` READY. The final scoped format, validator, and diff checks passed on the combined P11-M02 changes after T003; they were not run as a separate T001 exit check.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. T002 must recheck task/dependency state and the validator before adding tests.

## P11-M02-T002 evidence

- Startup: after T001, the PowerShell 7 validator selected `P11-M02-T002` as the first READY task; `P11-M02-T001` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused operational tests were written before the implementation change. The Release build passed, and the focused run discovered 9 tests with 1 runtime failure: `ReleaseRequiresAnAssociatedPositionSnapshot` released a reservation when the checkpoint was closed but no linked position snapshot existed. The failure was in runtime behavior, not setup or test discovery.
- GREEN: `RecordObservation` evidence now has explicit exact persistence/reopen coverage for quantity, price, nullable fee, event/exchange/receive/availability times, and stable sequence. Sequential partial fills retain the full reservation and capacity totals; duplicate observations remain idempotent; identity conflicts, overfill, stale revisions, and checked quantity overflow leave no partial rows. Release now requires the current acknowledged checkpoint, a closed intent, and at least one latest associated closed position snapshot; a closed intent alone or a closed snapshot with an open intent fails. Repeated release is idempotent and saved risk evidence remains after release.
- Focused GREEN: `SqliteOperationalStateTests` passed 9/9 after the minimum guard. The affected Infrastructure.Tests Release build with `--warnaserror` passed with zero warnings/errors.
- Documentation: persistence and execution architecture notes now record the operational durability and release conditions. `docs/TASKS.md` marks `P11-M02-T002` DONE and `P11-M02-T003` READY. The final scoped format, validator, and diff checks passed on the combined P11-M02 changes after T003; they were not run as a separate T002 exit check.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. T003 must recheck task/dependency state and the validator before adding tests.

## P11-M02-T003 evidence

- Startup: after T002, the PowerShell 7 validator selected `P11-M02-T003` as the first READY task; `P11-M02-T002` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- TDD RED: focused operational tests were written before the implementation change. The Release build passed, and the focused run discovered 14 tests with 4 runtime failures: acknowledgement accepted checkpoint unknown/manual markers and latest actual manual/unknown position origins, and the recovery fixture could not observe the required rejection. The inconsistent-link rollback expectation was corrected to the already acknowledged revision-0 state and was not counted as a product failure.
- GREEN: acknowledgement now refuses checkpoint markers and latest actual `Manual`/`Unknown` position origins inside its immediate transaction, even when checkpoint markers omit them. `CanRetry`, new reservation validation, and release use the same latest-origin guard in their transactions; observations reset acknowledgement, stale acknowledgements fail, and an empty portfolio requires explicit revision-zero acknowledgement. Recovery after a later known snapshot and explicit reconciliation persists across restart. Known open positions continue to retain their reservation capacity.
- Focused GREEN: `SqliteOperationalStateTests` passed 14/14; `SqlitePortfolioReservationStoreTests` passed 11/11; `SqliteRiskReservationTests` passed 6/6. The complete affected Infrastructure.Tests Release run passed 89/89. The Release build with `--warnaserror`, scoped format verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Documentation: persistence, execution, and risk-model architecture notes now record the schema-11 risk evidence, authenticated reservation boundary, partial-fill/release conditions, and latest-origin reconciliation guard. `docs/TASKS.md` marks `P11-M02-T003` DONE and exposes `P12-M01-T001` plus `P13-M01-T001` as READY.
- Scope: no Core contract, project graph, package, order submission, execution adapter, `graft/`, or canonical source change was made; no commit or push was performed. Implementation stops after the P11-M02 block; the next READY tasks are confirmed by the final validator.

## P12-M01-T001 evidence

- Startup: the bundled PowerShell validator selected `P12-M01-T001` as the first READY task; `P11-M02-T003` was DONE and `P13-M01-T001` remained independently READY. The canonical source SHA-256 remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
- Official protocol review: on 2026-10-24, the current Binance USDⓈ-M Trade, Account, and User Data Streams documentation was rechecked for signed/API-key authentication, timestamp and `recvWindow` limits, order/Algo paths and fields, error classes including HTTP 503 unknown outcomes, order-history retention, stream event envelopes, and position freshness. Unknown freshness policy and undocumented nested Algo fields remain explicit unknowns; no endpoint or credential was used.
- TDD RED: after adding the profile fixtures and a compile-only contract shell, the direct Release DLL run discovered 9 tests and failed 9 at runtime with `NotImplementedException` from the absent adapter behavior. The test class was present, the DLL executed, and the selection was non-zero.
- GREEN: `IBinancePrivateRestClient`, signed REST/API-key stream operations, exact-decimal order/trade/position DTOs, One-way/BOTH fail-closed mode checks, injected private stream parser/transport, causal metadata, delta `ACCOUNT_UPDATE`, fill mapping, opaque `ALGO_UPDATE`, bounded responses, and secret-safe unknown-outcome errors are implemented. Focused `BinancePrivateAdapterTests` passed 12/12; full `TradingBot.Exchange.Binance.Tests` passed 50/50. The Release test build with `--warnaserror` passed with 0 warnings and 0 errors; scoped format verification passed; docs validation and `git diff --check` passed after this transition.
- Changed files: `src/TradingBot.Exchange.Binance/BinancePrivateContracts.cs`, `BinancePrivateRestClient.cs`, `BinancePrivateStreamParser.cs`, `BinancePrivateWebSocketClient.cs`, `tests/TradingBot.Exchange.Binance.Tests/BinancePrivateAdapterTests.cs`, `docs/architecture/private-protocol.md`, `docs/architecture/execution-model.md`, `docs/TASKS.md`, and this handoff.
- Scope and limits: no Core contract, SQLite schema, durable execution state, real/private network request, production activation, package, project-graph, `graft/`, canonical source, commit, or push was changed. Hedge/unknown position mode, unknown Algo operations, unknown outcomes, and missing freshness evidence remain blocked until later reconciliation work.
- After validation, `P12-M01-T002` is READY and `P13-M01-T001` remains independently READY. T002 must recheck task/dependency state and the validator before adding tests; no Sol post-audit was run.

## P12-M01-T002 evidence

- Startup: after T001, the PowerShell validator was re-run and selected
  `P12-M01-T002` as the first READY task; `P12-M01-T001` was DONE and
  `P13-M01-T001` remained independently READY. No user changes, `graft/`,
  canonical source, package, or project-graph files were altered.
- TDD RED: focused Release DLLs were compiled with compile-only shells and
  executed directly with the required xUnit runner. Core discovered 6 and
  failed 6 at runtime with `NotImplementedException`; Infrastructure
  discovered 5 and failed 5 at runtime; App coordinator integration
  discovered 2 and failed 2 at runtime. No RED relied on a missing class,
  compile failure, or zero-test selection.
- GREEN: Core adds BCL-only deterministic lifecycle states and observations for
  accepted, rejected, partial, filled, cancel-requested, cancelled, timeout,
  unknown, and conflict outcomes. Exact receipts are idempotent; conflicting
  receipts, overfills, terminal mutations, timeout, and unknown outcomes fail
  closed. `ExecutionStateMachineTests` passed 6/6 and full Core.Tests passed
  277/277.
- GREEN: Infrastructure adds SQLite schema migration 11 -> 12 with durable
  `execution_states`, `execution_attempts`, and `execution_receipts`. The new
  `SqlitePortfolioOperationalStore` atomically verifies the acknowledged
  reconciliation revision, creates a deterministic dispatch claim, persists
  observations and receipts, and reconstructs state after restart. Sequential
  and concurrent duplicate claims cannot create a second attempt; unknown and
  conflicting receipts remain reconciliation-required. Focused execution
  tests passed 6/6 and full Infrastructure.Tests passed 95/95.
- GREEN: App `ExecutionCoordinator` requires an approved existing
  `RiskEvaluationResult`, invokes the existing atomic reservation/intent store,
  then dispatches only after a durable `Claimed` result. `AlreadyClaimed` is
  blocked without another adapter call; timeout and adapter exceptions become
  durable timeout/unknown observations. Focused coordinator tests passed 2/2
  and full IntegrationTests passed 8/8. The adapter remains injected and no
  private network or real order is reachable from these fixtures.
- Verification: affected Core.Tests, Infrastructure.Tests, and
  IntegrationTests Release builds with `--warnaserror` passed with zero
  warnings/errors; scoped `dotnet format --verify-no-changes`,
  `tools/validate-docs.ps1`, and `git diff --check` passed. No commit, push,
  production activation, or automatic Sol audit was performed.
- Changed files: `src/TradingBot.Core/ExecutionLifecycleContracts.cs`,
  `tests/TradingBot.Core.Tests/ExecutionStateMachineTests.cs`,
  `src/TradingBot.Infrastructure/SqliteDatabase.cs`,
  `src/TradingBot.Infrastructure/SqliteExecutionStateStore.cs`,
  `tests/TradingBot.Infrastructure.Tests/SqliteExecutionStateTests.cs`,
  affected schema-version assertions in Infrastructure.Tests,
  `src/TradingBot.App/ExecutionCoordinator.cs`,
  `tests/TradingBot.IntegrationTests/ExecutionCoordinatorTests.cs`,
  `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, `docs/TASKS.md`, and this handoff.
- After validation, `P12-M01-T003` is the first READY task and
  `P13-M01-T001` remains independently READY. T003 must recheck status,
  dependencies, and the validator before adding restart-reconciliation tests;
  implementation stops at T002 until that check.

## P12-M01-T003 evidence

- Startup: after T002, the validator was re-run before implementation and
  selected `P12-M01-T003` as the first READY task; `P12-M01-T002` was DONE and
  `P13-M01-T001` remained independently READY. No user changes, `graft/`,
  canonical source, package, or project-graph files were altered.
- Official protocol boundary retained: recovery uses only injected adapter
  evidence. It does not call Binance, submit an order, retain credentials, or
  activate production/private execution. Position mode remains One-way/BOTH;
  unknown and Hedge modes stay blocked.
- TDD RED: the compile-only recovery shell built successfully, then the direct
  Release DLL discovered 5 tests and failed all 5 at runtime with
  `NotImplementedException`. The failures covered restart/no-order retry,
  limited-history absence, duplicate post-commit evidence, partial-fill/cancel
  ordering, and an injected interruption after the external result. No missing
  class, compile failure, or zero-test selection was used as RED.
- GREEN: Core adds explicit recovery evidence status, completeness/history flags,
  and exact fill/position/reservation identity sets. The Infrastructure facade
  reconstructs fills and local operational facts, binds replay to the current
  acknowledged revision, and authorizes retry only after complete fresh no-order
  evidence. The App coordinator replays reconciliation observations idempotently
  and blocks stale, unknown, manual, conflicting, incomplete, limited-history,
  or identity-mismatched evidence. Durable facts are checked before retry and a
  fresh no-order recovery advances the dispatch attempt only after explicit
  recovery.
- Focused GREEN: `ExecutionRecoveryTests` passed 7/7; Core recovery tests 3/3;
  related Core state-machine tests 6/6; Infrastructure execution tests 6/6,
  operational regressions 14/14, and reservation regressions 11/11; coordinator
  regressions 2/2. The affected Core, Infrastructure, App, Core.Tests,
  Infrastructure.Tests, and IntegrationTests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format write and
  `--verify-no-changes`, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Changed files: `src/TradingBot.Core/ExecutionRecoveryContracts.cs`,
  `src/TradingBot.Infrastructure/SqliteExecutionStateStore.cs`,
  `src/TradingBot.App/ExecutionCoordinator.cs`,
  `tests/TradingBot.Core.Tests/ExecutionRecoveryStateTests.cs`,
  `tests/TradingBot.IntegrationTests/ExecutionRecoveryTests.cs`,
  `docs/TASKS.md`, `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, and this handoff. Earlier T001/T002 files
  remain part of the uncommitted P12-M01 working tree.
- Scope: no commit, push, Sol post-audit, real/private network request, order
  submission, production activation, package, project-graph, canonical source,
  or `graft/` change was made. The next READY tasks after final validation are
  `P12-M02-T001` and the independent `P13-M01-T001`; implementation stops here.

## P12-M02-T001 evidence

- Startup: the required project documents, clean working-tree status, and
  `tools/validate-docs.ps1` were rechecked. `P12-M02-T001` was the first READY
  task with `P12-M01-T003` DONE; `P13-M01-T001` remained independently READY.
- TDD RED: the focused Release IntegrationTests assembly built successfully,
  then the direct xUnit DLL run discovered 4 tests and failed all 4 at runtime
  because the development harness and durable single-use claim behavior were
  absent. RED did not rely on a compile failure, missing test class, or zero
  tests.
- GREEN: App adds a sealed, programmatic-only
  `PrivateExecutionDevelopmentHarness` over the existing dispatcher and
  reconciler seams. It accepts only stores and scripted Core observations; it
  exposes no URI, HTTP transport, Binance client, credential, or activation
  input. Infrastructure migrates SQLite schema 12 -> 13 with the durable
  `development_dispatch_claims` table. The claim is exact and single-use,
  survives restart, requires a persisted intent and reservation, and rejects
  forged/conflicting claims before any scripted dispatcher call. The scripted
  reconciler remains synthetic and fail-closed when evidence is absent.
- GREEN evidence: `PrivateExecutionDevelopmentHarnessTests` passed 7/7;
  affected `Infrastructure.Tests` passed 95/95 and `IntegrationTests` passed
  22/22. Infrastructure, App, and IntegrationTests Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` all passed.
- Scope: no private network request, credential, real order, production
  activation, package, project-graph, canonical source, `graft/`, commit, push,
  or automatic Sol audit was performed. The next READY task is
  `P12-M02-T002`; status, dependencies, and the validator must be rechecked
  before its TDD cycle begins.

## P12-M02-T002 evidence

- Startup: after T001, the task table and validator were rechecked. The first
  READY task was `P12-M02-T002`, its dependency `P12-M02-T001` was DONE, and
  `P13-M01-T001` remained independently READY.
- TDD RED: the focused Release IntegrationTests DLL discovered 5
  `ProtectiveExecutionTests` and failed all 5 at runtime because the protective
  request and claim path were absent. The focused Exchange.Binance.Tests DLL
  discovered 4 `ReduceOnlyOrderMappingTests` and failed all 4 at runtime because
  the protective mapper was absent. Both DLLs compiled successfully; neither RED
  used a missing test class, compile failure, or zero-test selection.
- GREEN: Core adds `ProtectiveExecutionRequest`, which requires a Stop,
  TakeProfit, or Emergency role, `ReduceOnly`, a known position owned by the
  source intent, matching instrument, opposite direction, and quantity within
  the known position. Infrastructure advances SQLite schema 13 -> 14 with an
  atomic `protective_dispatch_claims` record bound to the durable execution
  attempt, position, source intent, and expected acknowledged revision. App
  obtains protective claims without entry risk or reservation capacity. The
  Binance mapper emits only One-way/BOTH, opposite-side, reduce-only mappings:
  Stop/TakeProfit use Algo market types and Emergency uses a market order; Hedge
  and unknown modes fail closed.
- GREEN evidence: `ProtectiveExecutionTests` passed 6/6 and
  `ReduceOnlyOrderMappingTests` passed 4/4. Affected Infrastructure.Tests
  passed 95/95, IntegrationTests 28/28, and Exchange.Binance.Tests 54/54.
  Core, Infrastructure, App, Exchange, and affected test Release builds with
  `--warnaserror` passed with zero warnings/errors. Scoped format verification,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Scope: protective claims remain development/test composition only. No private
  network request, real order, production activation, credential, package,
  project-graph, canonical source, `graft/`, commit, push, or automatic Sol
  audit was performed. `P12-M02-T003` was READY and its status/dependency
  preflight plus validator were rechecked before the next TDD cycle.

## P12-M02-T003 evidence

- Startup: `P12-M02-T002` was DONE and `P12-M02-T003` was the first READY task;
  `P13-M01-T001` remained an independent READY task. The validator passed before
  tests and no unrelated block was started.
- TDD RED: the focused Release IntegrationTests assembly built successfully,
  then the direct xUnit DLL discovered 4
  `ActivationSeparationEvidenceTests` and failed all 4 at runtime because the
  activation-separation evidence builder was absent. RED did not use a missing
  test class, compile failure, or zero-test selection.
- GREEN: App adds `ActivationSeparationEvidence` and a static
  `ActivationSeparationEvidenceBuilder` that accepts only the sealed
  `PrivateExecutionDevelopmentHarness`, bounded provenance labels, a captured
  UTC timestamp, and a nonzero fully passing profile. The builder records a
  deterministic identity and scripted dispatch count while fixing private
  transport, credentials, and real-money activation facts closed; it exposes no
  activation method, endpoint, signer, or credential input. No SQLite schema or
  production composition change was added.
- GREEN evidence: `ActivationSeparationEvidenceTests` passed 4/4;
  `PrivateExecutionDevelopmentHarnessTests` passed 7/7,
  `ProtectiveExecutionTests` 6/6, and `ExecutionCoordinatorTests` 2/2.
  Affected App and IntegrationTests Release builds with `--warnaserror` passed
  with zero warnings/errors. Scoped format write and `--verify-no-changes`,
  `tools/validate-docs.ps1`, and `git diff --check` passed.
- Official protocol and prior-stage evidence remains unchanged: T001 RED was
  4/4 runtime failures and GREEN was 7/7; T002 recorded the official Binance
  private REST/user-stream boundary in the isolated adapter, migrated SQLite
  schema 13 -> 14 for protective claims, and passed its focused 6/6 and 4/4
  profiles plus affected regressions. T003 adds no private network call.
- Changed files for T003: `src/TradingBot.App/ActivationSeparationEvidenceBuilder.cs`,
  `tests/TradingBot.IntegrationTests/ActivationSeparationEvidenceTests.cs`,
  `docs/TASKS.md`, `docs/architecture/execution-model.md`,
  `docs/architecture/persistence.md`, and this handoff. Earlier P12-M02 files
  remain part of the uncommitted working tree.
- Scope: no commit, push, Sol post-audit, private network request, credential,
  real order, production activation, package, project-graph, canonical source,
  or `graft/` change was made. Final validation must leave
  `P13-M01-T001` as the next READY task; implementation stops here.

## P13-M01-T001 evidence

- Startup: the worktree was clean and `tools/validate-docs.ps1` confirmed
  `P13-M01-T001` READY with both dependencies DONE.
- TDD RED: the focused Release IntegrationTests DLL discovered 2
  `LaneCHistoryCaptureTests` and failed both at runtime because the Lane C
  capture pipeline and depth metadata were absent.
- GREEN: Core capture provenance now optionally retains typed Binance depth
  `U`/`u`/`pu`, recovery generation, snapshot identity, and the original
  per-session stable sequence. The Binance recovered-book state carries REST
  snapshot event/transaction times independently of the diff event that first
  aligns the book. App adds `LaneCHistoryCapturePipeline`, which assigns one
  durable capture/stable order to snapshots, public depth/trades, and source or
  recovery diagnostics; after a gap, depth capture remains closed until a new
  ready snapshot. Existing bounded Parquet persistence and old capture rows are
  preserved without a schema bump.
- GREEN and adjacent evidence: `LaneCHistoryCaptureTests` passed 3/3,
  `CaptureContractTests` 3/3, `ParquetCaptureStoreTests` 4/4, and
  `BinanceDepthAndOrderBookTests` 6/6. Release builds for Core.Tests,
  Infrastructure.Tests, Exchange.Binance.Tests, and IntegrationTests passed
  with zero warnings/errors. Scoped format verification, documentation
  validation, and `git diff --check` were run before marking the task DONE.
- Scope: no private network request, credential, order, execution path,
  package, project graph, canonical spec, `graft/`, commit, or push was used.
  The next READY task is P13-M01-T002; status, dependencies, and validator must
  be rechecked before its TDD cycle begins.

## P13-M01-T002 evidence

- Startup: after T001, the status/dependency preflight and documentation
  validator selected `P13-M01-T002` READY.
- TDD RED: the focused Release Core.Tests DLL discovered one
  `LaneCFeatureTests` case and failed at runtime because the Core evaluator was
  absent.
- GREEN: BCL-only Core adds versioned immutable Lane C input, configuration,
  per-price features, and results. The pure evaluator applies a bounded
  causal cut, reconstructs depth from snapshot/diff rows, checks Binance
  sequence continuity, and computes spread, top-N depth, book/aggressor
  imbalance, price-level changes, wall persistence/replenishment/withdrawal/
  migration candidates, and absorption candidates only under caller-supplied
  criteria. No feature threshold has a numeric default; absent evidence and
  thresholds remain explicit reasons/null results. Canonical input plus
  configuration determines a stable SHA-256 input identity.
- GREEN and adjacent evidence: `LaneCFeatureTests` passed 8/8, including
  hand-calculated decimal outputs, window boundaries, no-lookahead, stale book
  and trades, recovery gaps, warmup, zero denominator, wall candidates,
  explicit unknown thresholds, deterministic identity, JSON round-trip, and
  bounded input. `CoreBoundaryTests` passed 2/2, `ArchitectureTests` 1/1,
  `CaptureContractTests` 3/3, and `LaneCHistoryCaptureTests` 3/3. Core.Tests
  and IntegrationTests Release builds passed with zero warnings/errors; scoped
  format verification passed.
- Scope: no persistence, package, private network, execution, order, canonical
  source, `graft/`, commit, or push change was made. The next READY task is
  P13-M01-T003; status, dependencies, and validator must be rechecked before
  its TDD cycle begins.

## P14-M02-T003 evidence

- Startup: the validator selected `P14-M02-T003` as the first READY task with
  `P14-M02-T002` DONE. The SDK was `10.0.401`; the canonical source SHA-256
  remained `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`;
  no `graft/` or canonical-source diff was present.
- TDD RED: after adding the target profile and compile-only shell, `dotnet build
  tests/TradingBot.IntegrationTests/TradingBot.IntegrationTests.csproj -c Release
  --warnaserror` passed with 0 warnings/errors. The direct Release DLL discovered
  6 `PromotionReviewRecordTests` and failed all 6 at runtime with
  `NotImplementedException`; no compile failure or zero-test selection was used.
- GREEN: App adds immutable SHA-256/content-addressed review and invalidation
  records. Verdicts are `Blocked`, `Rejected`, and `AcceptedForReviewGate` and
  callers must supply the verdict explicitly; no auto-accepting review
  convenience remains. Acceptance is fail-closed for blockers, unknowns,
  Missing/Unknown/Invalidated evidence, and synthetic provenance. Records
  retain the canonical linked bundle, definition/report/bundle identities,
  review reasons, provenance, exclusions, every evidence reference/status and
  nullable causal field, blockers, and unknowns. All verdicts expose no
  execution authority.
- Durability: `PromotionReviewJournal` stores reviews and invalidations in
  separate content-addressed directories. Each canonical document is written
  to a flushed CreateNew temp file and atomically moved without overwrite;
  identical retries are idempotent, filename/content identity is checked on
  reopen, partial temp files are ignored, and tampered rows fail closed.
  Invalidation records compare the full bundle identity (including
  provenance-only changes), retain changed context/evidence/categories/reasons,
  link to the original decision, and permanently remove it from the active
  view; a new review attempt identity is required after invalidation. Same
  native evidence identifiers with changed canonical content are recorded as
  affected evidence categories while the original review remains unchanged.
- GREEN evidence: the direct `PromotionReviewRecordTests` profile passed 10/10.
  Adjacent profiles passed `PromotionEvidenceBundleTests` 4/4,
  `FrozenOosEvaluationTests` 11/11, `ResearchFreezeTests` 4/4,
  `PromotionCriteriaTests` 3/3, `MultiLaneDecisionTraceRegressionTests` 3/3,
  and `ActivationSeparationEvidenceTests` 4/4. App, IntegrationTests, and
  Replay.Tests Release builds passed with 0 warnings/errors; scoped format
  verification, documentation validation, and `git diff --check` passed.
- Evidence boundary: only synthetic OOS/shadow/simulation fixtures exist.
  Operational shadow, local-paper, exchange-test, performance, and
  reconciliation proof remain unresolved; no promotion or execution authority
  is granted. No package, schema migration, private network, credential, order,
  activation, canonical source, `graft/`, commit, or push was used.
- After final validation, `P15-M01-T001` is READY and implementation stops at
  the end of P14.

## P15-M01-T001 evidence

- TDD RED: the initial Release DLL profile discovered 7 tests and failed 7/7 at
  runtime with `NotImplementedException`; the later risk-safety profile
  discovered 16 tests and failed 2/16, then runtime GREEN passed 16/16. The
  command profile passed 1/1.
- GREEN and validation: isolated SQLite journal, restart, and kill-switch
  checks passed. Infrastructure, App, and IntegrationTests Release builds with
  `--warnaserror` passed with zero warnings/errors. Project-wide Infrastructure
  format passed after two pre-existing files were normalized to BOM+CRLF
  without code changes; changed-file format passed for 11 C# files. Default
  evaluation without explicit configuration fails closed and real order count
  remains zero in the tested path.
- Limitations: real Binance network, Linux/systemd deployment, and 24/7 soak
  remain unverified. No real-world trading evidence or activation is claimed.

## P15-M01-T002 evidence

- Startup: the required project documents, Git status, SDK `10.0.401`, and
  `tools/validate-docs.ps1` were rechecked. `P15-M01-T001` was DONE and the
  validator selected `P15-M01-T002` as the first READY task. The existing
  `docs/architecture/operations.md` working-tree marker was preserved while
  the T002 operations section was updated.
- TDD RED: the IntegrationTests Release build completed with zero
  warnings/errors; the direct DLL discovered one
  `OperationalShadowObservabilityTests` test and failed at runtime because
  `OperationalShadowRuntime.HealthSnapshot` was absent. RED did not use a
  compile failure or zero discovery.
- GREEN: App exposes immutable `OperationalShadowHealthSnapshot` state and a
  `HealthChanged` event. The snapshot records feed freshness, last receive and
  availability, stale/gap, reconnect, recovery, persistence, kill-switch,
  gate reasons, zero real orders, and explicit
  `NotApplicableInShadow` private-reconciliation status. A health source can
  publish market and depth state independently; the runtime combines those
  states fail-closed, refreshes on source diagnostics and a five-second timer,
  and keeps the existing decision/risk shadow path unchanged. BCL metrics add
  bounded health, stale/gap, reconnect/recovery, persistence, gate,
  kill-switch, reconciliation-applicability, and zero-real-order gauges.
  Structured JSON includes startup, health, cycle, stop, and fault records.
  Required public stream completion cancels its peer and faults the command so
  systemd can restart it; depth recovery must return to Ready after a gap.
- Acceptance: `OperationalShadowObservabilityTests` 3/3,
  `OperationalShadowRuntimeTests` 16/16,
  `OperationalShadowCommandTests` 2/2,
  `BinanceWebSocketClientTests` 8/8,
  `BinanceDepthAndOrderBookTests` 6/6, and Binance `ArchitectureTests` 1/1
  passed. App, IntegrationTests, and Exchange.Binance.Tests Release builds
  with `--warnaserror` passed with zero warnings/errors. Scoped format
  verification, `tools/validate-docs.ps1`, and `git diff --check` passed.
- Scope and limits: no private or real order path, credential, package,
  project-graph, canonical source, or `graft/` change was made. Linux/systemd
  execution, real Binance connectivity, and long-duration soak remain
  unverified; `P15-M01-T003` is the next READY task for bounded soak and
  restart/replay evidence.

## P15-M01-T003 evidence

- Startup: Git was clean before the prior executor added the T003 fixture;
  `P15-M01-T002` was DONE and the docs validator selected `P15-M01-T003` as
  first READY. The test file remained untracked after an initial `dotnet` PATH
  failure. This session used SDK `10.0.401` by its installed absolute path.
- GREEN: `OperationalShadowSoakRecoveryTests` passed 4/4. The bounded local
  fixture processes 256 deterministic observations across four process
  lifetimes, compares complete persisted rows with an uninterrupted run,
  checks all four causal times and stable sequence, derived state, outcome and
  checkpoint JSON, verifies sequence-255 recovery and duplicate idempotence,
  exercises reconnect/gap gating and held SQLite writer recovery, and asserts
  zero real orders in runtime and structured JSON evidence.
- Acceptance: related shadow integration tests passed 25/25, Binance public
  reconnect/depth/architecture tests 15/15, risk-math replay 2/2, and
  multi-lane decision trace regression 3/3. The IntegrationTests Release build
  and all transitive production projects passed with `--warnaserror`, zero
  warnings and zero errors. Scoped format verification passed. The final docs
  validator and staged diff checks are recorded by the completing run.
- Limits: these are synthetic local fixtures. Real Binance connectivity,
  Linux/systemd operation, and a 24/7 soak remain unverified. No private
  network, credential, real order, activation, package, schema, or canonical
  source change was made. `P15-M02-T001` is next READY and was not started.

## P15-M02-T001 evidence

- Startup: `AGENTS.md`, the project specification, execution plan, task graph,
  and prior handoff were read; the worktree was clean before the task. The
  validator selected `P15-M02-T001` as the first READY task after
  `P15-M01-T003`; the independent later tasks remained blocked by dependency.
- TDD RED: the new focused IntegrationTests DLL discovered 4 quality tests and
  failed at runtime on the missing coverage surface. The Infrastructure.Tests
  DLL discovered 2 health-evidence tests and failed at runtime because the
  evidence type was absent and schema version 2 had not been migrated. The RED
  runs were not compile failures or zero-test selections.
- GREEN: `IOperationalShadowHealthSource` now exposes a nullable required-feed
  identity set. Coverage deduplicates required identities and covered rows,
  requires receive plus availability evidence, ignores extra rows for the
  denominator, reports expected/covered counts, nullable ratio, and missing
  reasons, and blocks incomplete or unknown coverage. Existing stale-after,
  gap/recovery, reconciliation NotApplicableInShadow, and zero-real-order
  signals remain intact. Health JSON and BCL coverage gauges include run ID and
  health sequence.
- Durability: `OperationalShadowStore` is schema v2. New files create the
  additive health-evidence table; v1 files migrate transactionally while
  preserving market journal/checkpoint rows. Versioned JSON evidence records a
  unique run, monotonic sequence, checked timestamp, and nullable causal
  checkpoint link. Initial/source/timer/cycle health snapshots persist; timer
  snapshots have no causal link, restart creates a distinct run, and a failed
  health write marks persistence unhealthy and closes the gate.
- Focused GREEN direct DLL results: `OperationalShadowHealthQualityTests` 7/7,
  `OperationalShadowHealthEvidenceTests` 2/2,
  `OperationalShadowObservabilityTests` 3/3,
  `OperationalShadowRuntimeTests` 16/16,
  `OperationalShadowCommandTests` 2/2,
  `OperationalShadowSoakRecoveryTests` 4/4,
  Binance `BinanceWebSocketClientTests` 8/8, `BinanceDepthAndOrderBookTests`
  6/6, and `ArchitectureTests` 1/1, Replay `RiskMathParityTests` 2/2 and
  `SimulatedRiskTests` 6/6, and integration multi-lane parity 3/3.
- Final local checks: affected Release builds used
  `C:\temp\dotnet10\dotnet.exe` SDK 10.0.401 with `--warnaserror` and passed
  with zero warnings/errors; scoped `dotnet format --verify-no-changes` passed
  for App, Infrastructure, IntegrationTests, and Infrastructure.Tests;
  `pwsh -NoProfile -File tools/validate-docs.ps1` passed and selected
  `P15-M02-T002` as next READY; `git diff --check` passed. At that task's
  completion, no commit or push had yet been made. T001 was subsequently
  committed and pushed as `eaf2b6d33b9cf0aa13840a5476648555a135aef4`.
  No private network request, credential, real order, package/project-graph,
  canonical-source, or `graft/` change was made.
- Limits: evidence is synthetic/local. Real Binance connectivity,
  Linux/systemd execution, and 24/7 soak remain unverified. `P15-M02-T002` is
  now READY and this implementation stops at T001.

## P15-M02-T002 evidence

- Startup: the required project documents, current Git status, SDK path, and
  `tools/validate-docs.ps1` were rechecked. `P15-M02-T001` was DONE and
  `P15-M02-T002` was the first READY task; no future phase was started. The
  prior uncommitted T002 store/App/test files were treated as partial work and
  repaired within the owned scope.
- TDD RED: after the focused audit tests were written, the Release profile
  first failed on the existing CA1859 warning-as-error and then compiled to the
  expected missing `OperationalShadowAuditCommand` surface. No RED relied on a
  missing test assembly or zero-test discovery.
- GREEN: `OperationalShadowStore.OpenReadOnly` and `ReadAuditSnapshot(runId)`
  use one deferred read-only SQLite transaction, validate schema/evidence
  metadata, retain every run health row, and select only journal rows named by
  that run's causal checkpoint links. The App audit builder preserves quality,
  coverage, gate/reasons, causal timestamps and JSON, reconciliation
  `NotApplicableInShadow`, and zero-real-order fields. Health sequence gaps and
  missing/unresolved causal links remain explicit blockers; corrupt metadata,
  unsupported versions, malformed causal/checkpoint JSON, and nonzero real
  orders refuse export. Canonical `operational-shadow-audit-v1` JSON is
  content-addressed with SHA-256. Export is atomic UTF-8, no-overwrite,
  idempotent for equal bytes, and rejects SQLite/WAL/SHM destinations. The
  offline `shadow-audit-export` command returns 0 for successful export even
  with blockers, 1 for export/refusal errors, and 2 for bad arguments.
- Focused GREEN: `OperationalShadowAuditExportTests` passed 8/8, covering
  deterministic restore/hash, run scoping, sequence/link blockers, unsupported
  and corrupt metadata, nonzero real-order refusal, atomic/idempotent UTF-8
  export, SQLite sidecar refusal, promotion evidence authority separation, and
  CLI exit codes. Affected Release builds with `--warnaserror` and scoped
  format verification passed with zero warnings/errors.
- Documentation/validation: `docs/architecture/operations.md`,
  `docs/architecture/persistence.md`, `docs/TASKS.md`, and this handoff record
  the audit boundary. `tools/validate-docs.ps1`, `git diff --check`, and the
  final changed-file review passed before commit/push. T002 was committed and
  pushed as `ce7612774868e2333b3e97a3fe3f107c4b66cd44`. No schema, package,
  project-graph, canonical source, `graft/`, credential, private network,
  real order, or production activation change was made.
- Limits: the export contains only synthetic/local shadow evidence. Private
  reconciliation is explicitly not applicable in shadow; real Binance
  connectivity, Linux/systemd operation, and long-duration soak remain
  unverified. `P16-M01-T001` was the next READY task; see its completion
  evidence below.

## P16-M01-T001 evidence

- Startup: required project documents, clean Git status, dependency `P15-M02-T002`
  DONE, SDK 10.0.401, and `tools/validate-docs.ps1` were checked. The validator
  selected `P16-M01-T001` as first READY; local `main` matched `origin/main`.
- GREEN: App adds immutable `LocalPaperConfiguration` with format version,
  configuration ID, fixed `local-paper` mode, explicit positive virtual equity,
  risk fraction, shared risk-evaluation configuration, and separately versioned
  fill assumptions. `Parse`, read-only `Load`, and `ToJson` reject unknown or duplicate properties, missing fields, unsupported versions/modes, and malformed
  nested contracts. Errors do not echo JSON values. No credentials, endpoint,
  environment override, storage, network, executor, or activation surface exists.
- Focused verification: configuration tests passed 8/8, `CompositionTests` 1/1,
  `ActivationSeparationEvidenceTests` 4/4, `RiskMathParityTests` 2/2, and
  `FillSimulationIsolationTests` 3/3. Locked IntegrationTests restore and
  IntegrationTests/Replay.Tests Release builds with `--warnaserror` passed with
  zero warnings/errors. Scoped format verification, docs validation, and
  `git diff --check` passed. Validator selected `P16-M01-T002` READY.
- Limits: no executor, persistence, recovery, exchange connection, credentials,
  real orders, activation, packages, canonical source, or `graft/` changes.
  T002 remains the next task and was not started. Remote CI is unverified.

## P16-M01-T003 evidence

- Startup: the required project documents, Git status, dependency graph, and
  validator were reread. T002 was DONE and the validator selected T003 READY;
  no exchange-test or later-phase task was started. No commit or push was
  performed.
- TDD RED: the focused IntegrationTests profile compiled, discovered one
  `LocalPaperEvidenceTests` case, and failed at runtime with
  `NotImplementedException`. This was a behavior RED, not a compile-only or
  zero-discovery result.
- GREEN: App adds immutable `LocalPaperEvidence` and a builder that reads the
  persisted run-scoped observation/fill journal plus recomputed virtual ledger.
  Canonical versioned SHA-256 evidence retains source/configuration/model/plan
  identities, decision/risk/plan/fill assumptions, causal event/exchange/
  receive/availability timestamps and stable sequences, fills, P/L, fees,
  slippage, reservations, blockers, unknowns, and synthetic provenance.
  `ToPromotionEvidenceSnapshot` emits the existing `LocalPaper` category with
  explicit no-real-orders and no-execution-authority exclusions. A bounded
  interruption seam covers before-intent, after-intent, before-commit, and
  after-commit-before-response restart points; pending intents recover and
  committed fills remain exactly once. The store refreshes checkpoint state for
  durable pre-simulation reservations so an interrupted reservation is
  recoverable without changing its optimistic revision.
- Acceptance: the focused T003 profile passed `LocalPaperEvidenceTests` and
  `LocalPaperDeterminismRecoveryTests` 10/10. It covers canonical round-trip
  and tamper rejection, uninterrupted/repeated/restarted trace equality,
  partial take-profit/open-position evidence, corruption fail-closed capture,
  configuration/model-change refusal, and all four interruption points. App
  and IntegrationTests Release builds with `--warnaserror` passed with zero
  warnings/errors; locked IntegrationTests restore, scoped format, docs
  validation, and `git diff --check` passed. The validator now selects
  `P16-M02-T001` READY.
- Limits: all paper inputs and fills remain synthetic/local. The evidence is
  read-only and does not authorize private execution, exchange-test orders,
  real orders, activation, network access, credentials, or numeric promotion
  thresholds. Remote CI remains unverified.

## P16-M01-T002 evidence

- Startup: required project documents, `git status`, dependency state, and the
  bundled docs validator were reread. T001 was DONE; the validator selected
  T002 as the first READY task. No commit or push was performed.
- TDD RED: the focused IntegrationTests Release DLL first discovered three
  store cases; the legacy-migration case failed at runtime (`user_version` was
  0 instead of 2) and the executor-surface case failed because the App executor
  was absent. After the continuation tests were added, the same focused profile
  discovered 10 tests and failed 2/10 at runtime because EndOfData residual
  quantity was incorrectly blocked. A follow-up equity profile then discovered
  one test and failed at runtime because a second application accepted stale
  initial equity after realized P/L. All RED runs were runtime failures, not
  compile-only or zero-discovery results.
- GREEN: Infrastructure adds schema-2 `LocalPaperStore` migration, durable
  observation intents/reservations, atomic fills and virtual ledger
  checkpoints, optimistic revisions, restart recovery, idempotent exact
  retries, conflict refusal, corruption/legacy fail-closed behavior, and the
  retained append/reopen API. App adds immutable `LocalPaperObservation`,
  application/result contracts, and `LocalPaperExecutor` using
  `RiskEvaluationEngine`, shared `IRiskMathCalculator`, and injected
  `IFillSimulator` through the existing `SimulatedRiskSimulator`.
- Focused GREEN: `LocalPaperStoreTests` passed 2/2 and
  `LocalPaperExecutorTests` passed 9/9. The tests cover legacy transactional
  migration, intent-before-simulation reservation, atomic fills/ledger,
  restart/idempotence, dynamic current-ledger equity with stale-equity refusal,
  EndOfData continuation without a new entry application,
  partial take-profit retention/release, equal-prefix-fill deduplication,
  changed-fill conflict refusal, missing protective evidence, unsupported
  partial fill, legacy recovery blocking, and configuration/model identity
  conflict. Persisted plan recovery uses the strict Core contract fields and
  keeps compatibility aliases from weakening validation.
- Adjacent checks: locked IntegrationTests restore passed; affected
  IntegrationTests, Replay.Tests, and Infrastructure.Tests Release builds with
  `--warnaserror` passed with zero warnings/errors. LocalPaperConfiguration
  passed 8/8; focused RiskMathParity, SimulatedRisk, FillSimulationIsolation,
  and Replay architecture checks passed 12/12; Infrastructure architecture
  passed 1/1. Owned-file scoped `dotnet format --verify-no-changes` passed.
  A whole-project IntegrationTests format invocation also reported existing
  line-ending/charset violations in non-owned `LocalPaperConfigurationTests.cs`;
  that unrelated file was preserved. `tools/validate-docs.ps1` output was
  `docs validation passed: phases=19; milestones=41; tasks=119;
  ready=P16-M01-T003`; `git diff --check` exited 0. The canonical source
  SHA-256 remains `77E87BF93E538101FC7558F5F73195DCCE58F3BFF2C887E5A290F1A82C16FD54`.
  The validator selects T003 READY after this transition.
- Limits: all inputs and fills are synthetic/local and deterministic. No
  private network, credentials, orders, activation, P16-M02, package/project
  graph, canonical source, `graft/`, commit, or push change was made. T003 is
  READY but was not started; remote CI remains unverified.

## P16-M02-T001 evidence

- Startup: the required project documents, clean `main` status, SDK `10.0.401`,
  dependency graph, and `tools/validate-docs.ps1` were reread. The validator
  selected `P16-M02-T001` as the first READY task after `P16-M01-T003`.
- TDD RED: the focused IntegrationTests Release build discovered the new
  configuration profile but failed to compile because
  `ExchangeTestConfiguration` was absent. A subsequent behavioral runtime RED
  discovered 17 tests and failed 3/17 because URL-like, spaced, and
  colon-containing configuration identities were accepted. These were real
  contract failures, not a zero-discovery or manufactured shell run.
- GREEN: App adds immutable `ExchangeTestConfiguration`,
  `ExchangeCredentialReferences`, and `ExchangeTestOperationalPolicies`.
  Strict format-1, case-sensitive JSON round-trips only
  `exchange-test`/`binance-usdm-test` metadata and the documented
  `demo-fapi.binance.com` REST and `demo-fstream.binance.com/private/ws/`
  private stream endpoints. Credential fields are test-scoped references for
  environment variables or an external secret store; no secret value is
  resolved, persisted, logged, or sent. Unknown/duplicate fields, unsupported
  versions, unsafe or production endpoints, inline credential/activation
  fields, and enabled production/network policies fail closed. Policy labels
  carry no numeric trading defaults.
- Acceptance: `ExchangeTestConfigurationTests` passed 17/17. The affected
  IntegrationTests Release build with `--warnaserror`, scoped
  `dotnet format --verify-no-changes`, `tools/validate-docs.ps1`, and
  `git diff --check` passed. No package, schema, network call, credential,
  order, activation, canonical source, `graft/`, commit, or push changed.
- Limits: T001 proves configuration metadata and rejection behavior only.
  Runtime credential resolution, test transport, private lifecycle,
  reconciliation, and external Binance connectivity belong to T002/T003 and
  remain unverified. The validator selects `P16-M02-T002` READY; P17 remains
  blocked.

## P16-M02-T002 evidence

- Startup: required project documents, `git status`, SDK `10.0.401`, dependency
  state, and `tools/validate-docs.ps1` were reread on `main`; T001 was DONE and
  the validator selected T002 READY. The current official Binance General Info,
  Trade API, User Data Streams, and USDⓈ-M schema pages were rechecked directly
  on 2026-11-01 before runtime changes. No Binance endpoint was called and no
  credential value was read by the agent.
- TDD RED: the focused Binance profile first discovered the existing adapter and
  failed 2/2 at runtime because signed payloads did not end in `signature` and
  failure diagnostics pointed to `fapi.binance.com` instead of the configured
  origin. Compile REDs then covered the missing HMAC signer, isolated receipt
  store, executor transport surface, and runtime credential provider. These were
  real missing/incorrect behaviors, not manufactured no-op tests.
- GREEN implementation: the private REST adapter now appends the HMAC-SHA256
  signature as the final encoded field, rejects changed-origin responses and
  redirects, maps 429/418 and authentication failures to redacted diagnostics,
  and reports a query-free configured test-origin request URI. The private WebSocket default
  is the dedicated `demo-fstream.binance.com/private/ws/` route and rejects the
  production stream host. Runtime composition resolves environment or injected
  external-secret-store references only inside the process, uses the documented
  demo REST/WS origins, disables redirects, and has no production fallback or
  activation path.
- GREEN executor: `ExchangeTestExecutor` revalidates approved risk, uses the
  existing atomic reservation and single-use durable claim boundary, and invokes
  the concrete test-only transport composed by `ExchangeTestRuntime` (the
  interface remains injectable for isolated fixtures). Duplicate/stale claims,
  timeout/cancellation/unknown transport outcomes, conflicting receipts,
  missing reduce-only protection for partial fills, stale/disconnected private
  streams, ALGO_UPDATE, and manual/unknown positions remain fail-closed and
  require reconciliation. `SqliteExchangeTestStore` isolates run/configuration/
  environment metadata and stores normalized private causal receipts only;
  raw frames, listen keys, signatures, credentials, and credential-bearing
  payloads have no storage representation.
- Focused GREEN: `BinancePrivateAdapterTests` passed 61/61; the new
  `ExchangeTestExecutorTests` passed 4/4; `ExchangeTestRuntimeTests` passed 4/4;
  `ExchangeTestAtomicPersistenceTests` passed 2/2; and
  `SqliteExchangeTestStoreTests` passed 2/2. Adjacent Release profiles also
  passed IntegrationTests 151/151 and Infrastructure.Tests 114/114. The local
  profile does not establish external Binance connectivity or real-money
  execution permission.
- Scope: no T003 evidence export, P17 work, canonical source, `graft/`, commit,
  or push was performed. The next validator-selected task is T003 READY; remote
  CI remains unverified until a real run exists for a later commit.

## P16-M02-T002 continuation audit

- A completion review found that the initial T002 GREEN profile exercised an
  injected fake `IExchangeTestTransport`; `ExchangeTestRuntime` exposed REST/WS
  clients but did not compose a test-only transport that could submit and
  reconcile through those clients. It also found that normalized private
  receipts and execution observations were persisted by separate SQLite
  transactions rather than one receipt/fill/lifecycle/checkpoint boundary.
- T002 is therefore `IN_PROGRESS` and T003 is `BLOCKED` pending a discovered
  RED for integrated runtime composition and atomic interruption/restart
  persistence, followed by focused GREEN and the required acceptance checks.
- No exchange credentials, network calls, production endpoint, live activation,
  T003 evidence export, P17 work, commit, or push was performed during this
  continuation audit.

## P16-M02-T002 completion evidence

- The continuation RED was runtime-discovered with the Release IntegrationTests
  DLL: 148 tests were discovered, and the two new contract checks failed because
  `ExchangeTestRuntime` did not compose an executor transport and the operational
  store had no atomic exchange-observation boundary. This was followed by the
  implementation and focused GREEN, not a placeholder or `NotImplementedException`.
- `ExchangeTestRuntime.CreateTransport`/`CreateExecutor` now constructs the
  internal `BinanceExchangeTestTransport` over the immutable Binance USD-M test
  configuration. It maps the durable client order ID through signed test REST,
  requires a fresh matching private-stream event, checks `/private/ws/<listenKey>`
  freshness and expiry, reconciles through private REST plus stream/position
  checks, and never permits production fallback. The integrated synthetic profile
  verifies test REST/WS hosts, final `signature` field, and durable identity.
- `SqlitePortfolioOperationalStore.ApplyExchangeTestObservation` and the shared
  `SqliteExchangeTestStore` transaction seam require one SQLite database for the
  exchange-test run. Receipts, lifecycle state, fills, position projections, and
  reconciliation checkpoints commit together; invalid projection input rolls the
  whole batch back, and a reopened store reads the committed batch idempotently.
  Different database paths fail closed before dispatch.
- Final focused GREEN: Binance adapter 61/61, exchange executor 4/4, runtime 4/4,
  atomic persistence 2/2, and isolated exchange receipt store 2/2. Adjacent
  IntegrationTests 151/151 and Infrastructure.Tests 114/114 passed. The full
  affected Release solution build with `--warnaserror` had zero warnings/errors;
  scoped format verification passed. `tools/validate-docs.ps1` selects
  `P16-M02-T003` READY. External Binance connectivity remains unverified and no
  real credentials, order, activation, T003 evidence export, P17 work, commit,
  or push was performed.

## P16-M02-T002 continuation audit 2

- A further review found that the composed transport opened the private stream
  only after submitting the order, so an immediate `ORDER_TRADE_UPDATE` could be
  lost. Partial entry fills were also marked unknown because no actual
  reduce-only protective order was submitted. Reconciliation discarded the
  queried position/mode facts, and `ALGO_UPDATE` was normalized without an Algo
  REST reconciliation path.
- T002 is `IN_PROGRESS`; T003 is `BLOCKED` pending runtime RED/GREEN covering
  stream-before-submit ordering, durable reduce-only protection and restart
  recovery, complete REST/stream/position reconciliation, and explicit
  fail-closed Algo handling. No credentials, production endpoint, live
  activation, T003 evidence export, P17 work, commit, or push was performed.

## P16-M02-T002 continuation audit 2 GREEN

- The required RED was then reproduced with the focused runtime profile: 9
  tests were discovered and 5 failed for the exact stream race, missing actual
  protection, unsafe protection-failure result, and incomplete reconciliation
  identities. The implementation now connects the private socket before REST
  submission, places and queries a reduce-only `STOP_MARKET` Algo order for
  the observed partial quantity, persists the protective receipt/position
  projection through the shared transaction boundary, and returns explicit
  fail-closed evidence for opaque `ALGO_UPDATE`, stale streams, bounded-history
  absence, and manual/unknown positions.
- GREEN: `ExchangeTestRuntimeTests` 9/9, `ExchangeTestExecutorTests` 4/4,
  Binance adapter 61/61, Infrastructure 114/114, and full IntegrationTests
  156/156. Locked restore, affected Release `--warnaserror` builds, scoped
  format, docs validation (`ready=P16-M02-T003`), `git diff --check`,
  canonical source SHA, and graft-ignore checks passed. External Binance
  connectivity, credentials, real orders, live activation, remote CI, commit,
  and push remain unverified/not performed. T002 is DONE; T003 is READY and
  P17 remains untouched.
