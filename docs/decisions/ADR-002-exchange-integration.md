# ADR-002 — Exchange integration

## Context

The first production venue is Binance USD-M perpetual futures. The official legacy .NET connector does not cover the required `/fapi` Futures surface.

## Decision

Implement a thin `TradingBot.Exchange.Binance` adapter over `HttpClient` and `ClientWebSocket`, exposing project-owned interfaces to Core. Official API docs are authoritative.

## Alternatives considered

Use the official connector as a runtime dependency; use the Python SDK through a sidecar; or build a generic exchange abstraction first. These add unsupported surface, process complexity, or premature scope.

## Consequences

Protocol schemas, retries, rate limits, signing, and current endpoint behavior remain adapter responsibilities. Endpoint freshness must be verified before implementation.
