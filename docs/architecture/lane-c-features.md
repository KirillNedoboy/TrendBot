# Lane C microstructure features

`LaneCFeatureEvaluator` is a deterministic BCL-only Core calculation over a
bounded set of `CaptureEnvelope` rows. Its immutable input pins the instrument
and a causal cursor; configuration requires formula version, time window,
freshness ages, top-N depth, and maximum input records. Wall quantity and
absorption criteria are nullable caller policy. Missing thresholds remain
unknown and never receive a trading default.

The evaluator first filters rows by event time, availability time, and stable
sequence at the cursor, then applies depth in recorded capture order. A depth
snapshot seeds the book; diffs require the recorded Binance `pu` chain and
recovery diagnostics invalidate the book until another snapshot. Identical
event identities are ignored; conflicting values produce conflict evidence.
The configured time window selects feature observations while earlier eligible
depth rows may still seed the reconstructed book.

Spread is best ask minus best bid. Top-N bid and ask depth sum their respective
displayed quantities. Book imbalance is `(bid depth - ask depth) / total
depth`; it stays unknown when total depth is zero. Aggressor imbalance uses the
same signed ratio for buy and sell trade quantity. Per-price adds and removals
are measured between adjacent reconstructed books; removal is not assumed to
be an execution. Replenishment, withdrawal, and migration are emitted as
price/quantity candidates with an explicit caller wall floor: migration
requires an equal floored quantity removed and added at different prices on
the same side in one transition. Withdrawal is a removal with no trade print
at that price between the two book event times.

Mid-price move compares the first and latest valid mid in the window.
Price-response-per-aggressor-volume is its absolute move divided by observed
aggressive volume. An absorption candidate is produced only when both minimum
volume and maximum move criteria are supplied; otherwise it stays null with
an unknown reason. Warmup, stale book/trade, missing snapshot, sequence gap,
duplicate, conflict, and zero-denominator states remain explicit. The result
identity hashes the canonical causal input and configuration, so repeated
evaluation of the same cut is reproducible. These descriptive features do
not authorize a decision or order.
