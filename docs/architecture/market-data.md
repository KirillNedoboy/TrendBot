# Market data architecture

The initial venue is Binance USD-M perpetual futures. The future adapter owns REST and WebSocket protocol details and exposes normalized Core events. Public market data is staged from exchange metadata and bars to trades, mark/funding, open interest, liquidations, depth, and local orderbook recovery.

Official current Binance documentation is the authority. The repository records URLs supplied in the source specification, but each endpoint task must verify current paths, schemas, weights, authentication, and error behavior before implementation. Missing or redirected links remain `NEEDS_VALIDATION`.
