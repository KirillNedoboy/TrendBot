# Lane C capture history

`LaneCHistoryCapturePipeline` in App composes the existing Binance public
stream/orderbook contracts with `ParquetCaptureStore`. Its caller supplies
already parsed WebSocket envelopes and the recovery generation associated with
depth events. A ready `BinanceOrderBookState` is recorded as a snapshot before
updates in that generation; a gap or recovery diagnostic clears the active
generation, and later depth events fail closed until another ready snapshot is
recorded. Public trades remain capturable while the book is recovering.

Each captured row retains its event time, exchange time when available, local
receive and availability times, session/stream/event identity, raw public
payload when available, and the source stream sequence. The pipeline assigns a
single increasing capture/stable sequence so depth and trade rows with equal
event timestamps replay deterministically by intake order. Depth provenance
also retains `U`, `u`, `pu`, snapshot-vs-update, and local recovery generation.
REST snapshot event and transaction times are kept separately from the times of
the diff that first made the reconstructed book ready.

Source duplicate, identity-conflict, gap, disconnect, and recovery diagnostics
are recorded as diagnostic capture rows. Detection and transport recovery stay
in the Binance adapter; this pipeline preserves the evidence and does not
infer a missing trade, exchange fill, or wall-clock threshold. Capture remains
bounded by the existing Parquet store options and has no private execution,
decision, risk, order, or credential path. Existing rows remain readable
because new depth/source-sequence provenance fields are optional and no
persistence schema or capture format version changed.
