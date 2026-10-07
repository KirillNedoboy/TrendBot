# Core contract evidence

This report records the contract and fixture evidence available at commit `6afd836b43760a45d7e7cf45a11817eb4adf3582` (`test(core): add reusable contract fixture vectors`). It covers P02-M03-T002 and uses the source and test files that exist in that commit.

## Contract surfaces and version identifiers

The current Core surface is a set of passive, immutable records and side-effect-free interfaces. Shared risk math and fill simulation remain separate contracts:

- [`IRiskMathCalculator`, `RiskMathInput`, and `RiskMathResult`](../../src/TradingBot.Core/DomainContracts.cs): the calculator exposes `Version` and `Calculate(RiskMathInput)`. The input snapshots the instrument, direction, equity, risk fraction, entry and protective-stop prices, fee, and slippage. The result carries the version, copied input, risk budget, loss per unit, raw quantity, and notional exposure.
- [`IFillSimulator`, `FillSimulationInput`, and `FillSimulationResult`](../../src/TradingBot.Core/FillSimulationContracts.cs): the simulator exposes `Version` and `Simulate(FillSimulationInput)`. The input is a hypothetical request plus a causal market snapshot and explicit prose assumptions. The result carries the copied input and research-only `ResearchFill` observations.
- The fill surface contains no `OrderIntent`, `RiskDecision`, risk-math, executor, or authorization state. It reports research observations and has no order submission path.

The following are the only version identifiers documented by this report. The values are current fixture examples; they do not define a global wire or release compatibility version.

| Contract member | Current value in the fixture catalog | Source | Interpretation |
|---|---|---|---|
| `RiskMathVersion.Value` | `risk-v1` | `tests/Fixtures/CoreFixtureVectors.cs` (`RiskVersionValue`) | Example value used by deterministic risk vectors |
| `FillSimulationVersion.Value` | `fill-v1` | `tests/Fixtures/CoreFixtureVectors.cs` (`FillVersionValue`) | Example value used by deterministic fill vectors |
| `Setup.ContextVersion` | `context-v1` | `tests/Fixtures/CoreFixtureVectors.cs` (`NewSetup`) | Context identifier carried by the setup fixture |

`RiskMathVersion` and `FillSimulationVersion` are non-empty ordinal string values. `Setup.ContextVersion` is also a required string. There is currently no global Core version and no global wire-schema version. The fixture strings `risk-v1` and `fill-v1` are examples for tests and are not released production versions. No migration or wire compatibility promise follows from them.

[`CoreJsonSerializer`](../../src/TradingBot.Core/CoreJsonSerializer.cs) emits compact camelCase JSON, writes enums as strings, and rejects numeric enum values, unknown members, missing required constructor parameters, and invalid domain values. [`MarketEvent`](../../src/TradingBot.Core/DomainContracts.cs) uses stable `$type` discriminators (`trade`, `bar`, `bookDelta`, `fundingUpdate`, and `oiUpdate`); [`CausalMarketEvent`](../../src/TradingBot.Core/CausalEventContracts.cs) preserves event, exchange, receive/availability, and stable-sequence fields. Collection-valued contracts deserialize from JSON arrays and expose immutable snapshots. These are serializer behaviors, not a shared schema-version declaration.

## Deterministic fixture catalog

`tests/Fixtures/CoreFixtureVectors.cs` is linked with `Compile` into `TradingBot.Core.Tests` and `TradingBot.Replay.Tests`. It uses BCL APIs and `TradingBot.Core` only; it is not included in `src/TradingBot.Core`. Factories create fresh values from fixed inputs: the base timestamp is `2026-01-02T03:04:05Z`, instrument examples are `BTCUSDT` and `ETHUSDT`, the money currency is `USDT`, IDs use ordinal comparison, and decimal examples retain their declared precision.

| Vector group | Count | Catalog source | Consumers | Evidence recorded |
|---|---:|---|---|---|
| Exception vectors | 42 | [`CoreFixtureVectors.ExceptionVectors`](../../tests/Fixtures/CoreFixtureVectors.cs) (`CoreExceptionVector`) | [`ExceptionVectorsDeclareExactExceptionAndParameterExpectations`](../../tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs) | Each case declares the expected exception type and `ArgumentException.ParamName`; coverage includes value bounds, OHLC/anchor/position invariants, causal timing/sequence, risk currencies/stops/result positivity, fill assumptions/snapshot visibility, unique fill IDs, and overfill |
| Strict JSON round trips | 28 | [`CoreFixtureVectors.JsonRoundTripVectors`](../../tests/Fixtures/CoreFixtureVectors.cs) (`CoreJsonRoundTripVector`) | [`JsonRoundTripVectorsAreStrictDeterministicAndCultureIndependent`](../../tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs); [`SharedVectorsSerializeAndRestoreWithoutAReplayRunner`](../../tests/TradingBot.Replay.Tests/CoreFixtureReplayTests.cs) | Fresh factories serialize and restore the same value; compact JSON is equal under `en-US` and `fr-FR`; unknown members and required fields remain governed by `CoreJsonSerializer` |
| Invalid JSON cases | 8 | [`CoreFixtureVectors.JsonInvalidVectors`](../../tests/Fixtures/CoreFixtureVectors.cs) (`CoreJsonInvalidVector`) | [`JsonInvalidVectorsRejectMissingUnknownAndInvalidFields`](../../tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs) | Two missing-field cases, two unknown-field cases, and four invalid-value/enum cases expect `JsonException` |
| Event comparisons | 6 | [`CoreFixtureVectors.EventComparisonVectors`](../../tests/Fixtures/CoreFixtureVectors.cs) (`CoreEventComparisonVector`) | [`FactoriesProduceFreshObjectsWithStableExpectedValues`](../../tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs) | Duplicate, identity conflict, earlier event time, later event time, same event time, and different instrument relations are checked against declared outcomes |
| Causal ordering | 1 catalog vector with 3 events | [`CoreFixtureVectors.CausalOrdering`](../../tests/Fixtures/CoreFixtureVectors.cs) (`CausalOrderingVector`) | [`CausalAndFillVectorsExposeTheirDeclaredExpectedValues`](../../tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs); [`SharedCausalVectorsRetainEventTimeAndStableSequenceOrder`](../../tests/TradingBot.Replay.Tests/CoreFixtureReplayTests.cs) | The visible order is `event-first -> event-second`; `future-event` is excluded by the cursor's event-time and stable-sequence bounds |

The catalog also provides reusable value factories for `Bar`, `Setup`, `Position`, immutable order-book snapshots, `Decision`, `RiskDecision`, causal envelopes, risk inputs/results, fill assumptions/inputs/results, and research fills. The tests verify defensive copies for immutable collections, fresh object identity, UTC normalization, and ordinal identity semantics. They run the same factories in Core and Replay tests so replay-facing checks do not require a replay runner.

## Boundary and determinism evidence

The Core tests in `tests/TradingBot.Core.Tests/CoreFixtureVectorTests.cs`, `CoreBoundaryTests.cs`, and `ArchitectureTests.cs` verify that the fixture assembly has no reference to the Exchange, Infrastructure, or App assemblies, and that the fixture source is linked only into the two test projects. Replay boundary coverage is in `tests/TradingBot.Replay.Tests/ArchitectureTests.cs`. The production project graph is asserted exactly as follows:

```text
TradingBot.Exchange.Binance -> TradingBot.Core
TradingBot.Infrastructure -> TradingBot.Core
TradingBot.Backtesting -> TradingBot.Core
TradingBot.App -> TradingBot.Backtesting, TradingBot.Core,
                  TradingBot.Exchange.Binance, TradingBot.Infrastructure
TradingBot.DataTool -> TradingBot.Backtesting, TradingBot.Infrastructure
```

`TradingBot.Core` has no project references and remains BCL-only. The fixture tests cover immutable snapshots, fixed decimal values, culture-independent serialization, and causal availability/order without adding runtime dependencies or execution behavior. The risk and fill vectors are evidence for local contract invariants; they do not implement risk calculations, fill generation, replay, persistence, or authorization.

## Evidence limits and known CI result

This report documents passive contracts only. There is no replay runner, risk-math implementation, fill-generation implementation, persistence boundary, or proof of migration compatibility in this scope. Calculation parity remains a later task, and fill algorithms, replay integration, persistence, and paper execution remain future work.

GitHub Actions run `37668922127` for commit `6afd836b43760a45d7e7cf45a11817eb4adf3582` completed with failure in `Verify formatting` on both `ubuntu-latest` and `windows-latest`. The run reports repository-wide line-ending/encoding formatting findings. That known CI result is preserved as evidence; fixing it is outside this documentation task. Local focused checks are reported separately in `HANDOFF.md`.
