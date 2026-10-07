# Финальное техническое задание
## Autonomous Multi-Lane Perpetual Futures Trading System
### Version 1.0 — Source of Truth

**Этот документ заменяет все предыдущие архитектурные планы.** В Codex следует передавать именно его. Предыдущие обсуждения остаются источником происхождения требований, но при конфликте архитектурных решений приоритет имеет этот документ.

Проект создаётся **с нуля**:

```text
repository = NONE
existing code = NONE
existing DB = NONE
existing CI = NONE
existing deployment = NONE
```

При этом торговая логика собрана из всех обсуждавшихся ранее направлений: скальпинг по структуре рынка, Prizrak-подобный ATR/context layer, динамический trend-state из последнего видео, старые pump/pullback/failed-reclaim идеи, OI/funding/liquidations, SqueezeGuard-подход, order flow, L2/heatmap и архитектура Lane A/B/C.

---

# 1. Итоговые архитектурные решения

| Вопрос | Финальное решение |
|---|---|
| Язык | **C#** |
| Runtime | **.NET 10 LTS** |
| Архитектура | Modular monolith |
| ОС | Linux VPS |
| Deployment | systemd |
| Initial exchange | **Binance USD-M Perpetual Futures** |
| Exchange integration | собственный thin REST/WebSocket adapter |
| Strategy core | полностью собственный |
| Event model | deterministic event-driven |
| Persistence | SQLite + Parquet |
| Logging | structured JSON → journald |
| CI | GitHub Actions |
| Tests | xUnit v3 |
| Live/backtest logic | единый Core |
| Docker | не использовать в V1 |
| Kafka/Kubernetes | не использовать |
| Telegram | output/alerts only |
| Autoexecution | предусмотрено, но включается только после Shadow/Paper gates |
| Главный стиль | **краткосрочный intraday/scalping** |
| HTF | контекст, а не длительность сделки |
| Lane C | confirmation/veto/research в V1 |
| ML | не использовать до накопления dataset |

.NET 10 является LTS и поддерживается до 14 ноября 2028 года. [Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy)

---

# 2. Почему C#/.NET, а не Python/NautilusTrader

NautilusTrader остаётся важным **reference implementation**: event-driven architecture, одинаковая стратегия для simulation/live, market-event abstraction, reconciliation и replay — хорошие образцы проектирования. Сам проект прямо позиционирует единый deterministic event-driven runtime для research/backtest/live. [GitHub](https://github.com/nautechsystems/nautilus_trader)

Но финальный продукт должен иметь собственные:

```text
FeatureEngine
SetupLifecycle
LaneA
LaneB
LaneC
DecisionEngine
RiskEngine
Execution state machine
Replay
```

Поэтому NautilusTrader **не runtime dependency**.

C#/.NET выбран из-за сочетания:

- статической типизации;
- производительности, достаточной для L2/order-flow;
- качественного async/network stack;
- `System.Threading.Channels`;
- хорошей поддержки Linux/systemd;
- `TimeProvider` для deterministic tests;
- долгого LTS;
- удобства разработки через Codex;
- меньшего runtime-risk, чем самостоятельная Python asyncio-система при большом числе параллельных потоков market data.

Rust не требуется: это не HFT с colocated latency в десятки микросекунд.

---

# 3. Exchange: Binance USD-M Futures

Первой production venue фиксируется:

```text
BINANCE
USD-M
PERPETUAL FUTURES
```

Причины:

- достаточное количество perpetual contracts;
- trades;
- L2;
- funding;
- Open Interest;
- liquidations;
- mark/index prices;
- полноценные order/account APIs;
- подходящая инфраструктура для Lane A/B/C.

Текущая официальная Binance документация USD-M включает market-data endpoints для exchange info, funding, OI, depth, klines, trades и др. [Разработчики Binance](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Exchange-Information)

### Важное решение по SDK

Официальный старый `.NET Binance connector` **не поддерживает `/fapi/*` Futures API и соответствующие Futures WebSocket streams**. [GitHub](https://github.com/binance/binance-connector-dotnet)

Поэтому:

```text
НЕ:
Trading Core → third-party Binance SDK

А:

Trading Core
     ↓
our interfaces
     ↓
TradingBot.Exchange.Binance
     ↓
HttpClient / ClientWebSocket
     ↓
Binance API
```

Официальный новый Binance Python USD-M SDK можно использовать **только как reference implementation схем и примеров**, поскольку он покрывает `/fapi/*`, REST и WebSocket. [GitHub](https://github.com/binance/binance-connector-python/blob/master/clients/derivatives_trading_usds_futures/README.md)

---

# 4. Что именно будет торговать бот

Основная система — **не позиционный бот на дни**.

Начальный целевой режим:

```text
HTF market context: 1H / 4H
          ↓
local setup: 5m
          ↓
1m / realtime trades / L2 confirmation
          ↓
intraday position
```

Типичное ожидание:

```text
несколько минут
→ десятки минут
→ иногда дольше внутри дня
```

Не предполагается автоматически переносить позиции на несколько суток.

Точное `MaxHoldingTime` является **NEEDS_VALIDATION** и определяется backtest/replay, а не придумывается заранее.

---

# 5. Источники торговой идеи

Финальная стратегия объединяет несколько ранее исследованных направлений, но **не смешивает их все в один entry condition**.

## 5.1 Базовые скальпинговые документы

Из ранее разобранных материалов сохраняются:

```text
market context
structure
levels
range
breakout
false breakout
stop sweep
retest
relative volume
trade speed
aggressive side
large trades
absorption
spread
depth
liquidity
structural invalidation
risk-based sizing
```

Главный принцип:

> Сигнал должен развиваться во времени. Один snapshot рынка не должен непосредственно создавать сделку.

---

# 6. Prizrak-подобный структурный слой

Мы **не копируем закрытый код** MikhailPrizrak или Prizrak Trend.

Создаётся независимая формализованная модель.

Она содержит:

```text
HTF context
dynamic support/resistance
fast MA
slow MA
ATR
ATR channel
center line
upper/lower bands
boundary rejection
center retest
order-block approximation
local POC
```

---

# 7. Dynamic Trend State из последнего видео

Видео с индикатором `Probability Theory` добавляет ещё один важный элемент:

```text
price above dynamic trailing support
→ BULL regime

break confirmed
→ line flips above price
→ BEAR regime
```

Это не отдельная торговая стратегия.

В нашей системе он становится:

```text
DynamicTrendState
```

и является частью Lane A / HTF context.

Не воспроизводим неизвестную оригинальную формулу.

Используем собственный алгоритм:

```text
confirmed pivots
+
ATR buffer
+
close-based break
+
role reversal
+
MA alignment
```

---

# 8. Базовый HTF Context

Состояния:

```text
BULL
BEAR
NEUTRAL
PENDING
```

Fast/slow MA:

```text
EMA default
SMA optional
WMA optional
RMA optional
```

Research seed:

```text
Fast = EMA 21
Slow = EMA 55
```

но это **NEEDS_VALIDATION**, а не утверждение о profitable parameters.

### BULL candidate

```text
HTF Close > dynamic support
AND
FastMA > SlowMA
```

### BEAR candidate

```text
HTF Close < dynamic resistance
AND
FastMA < SlowMA
```

Context flip выполняется только после:

```text
N confirmed HTF closes
```

где:

```text
N = 1..3
research seed = 2
```

Незакрытый HTF bar никогда не меняет confirmed state.

---

# 9. Dynamic Support / Resistance

Использовать только подтверждённые pivots.

Например:

```text
PivotLeft
PivotRight
```

Pivot в `bar N` становится известным только после:

```text
N + PivotRight
```

То есть никаких будущих данных.

Bull support:

```text
last confirmed structural low
```

Bear resistance:

```text
last confirmed structural high
```

Добавляется:

```text
ATR × LevelBuffer
```

Тень уровня не ломает.

Break:

```text
close beyond level + buffer
```

Тогда допускается:

```text
support → resistance
resistance → support
```

---

# 10. Local ATR Channel

На Setup TF:

```text
center
upper
lower
```

Default:

```text
center = EMA(close, CenterPeriod)

width = ATR(ATRPeriod) × multiplier

upper = center + width
lower = center - width
```

Поддерживаем alternative width:

```text
StandardDeviation
```

Center algorithm enum:

```text
EMA
RMA
HLC3_SMOOTHED
```

Строковых «пользовательских формул» в production bot не будет.

---

# 11. Primary strategy V1

В первой версии для реального исследования активируются только **два setup-family**.

## SETUP-01 — Boundary Rejection

### LONG

```text
HTF Context = BULL

AND

Local Trend = BULL

AND

Low <= LowerBand

AND

Close > LowerBand

AND

bar confirmed

AND

minimum channel width passed

AND

data quality passed
```

Дополнительные filters могут использовать:

```text
volume
POC
OrderBlock
Lane B
Lane C
```

### SHORT

Зеркально:

```text
HTF Context = BEAR
Local Trend = BEAR
High >= UpperBand
Close < UpperBand
```

---

# 12. SETUP-02 — Center Retest

### LONG

```text
HTF = BULL

local bias = BULL

Low <= Center

Open >= Center

Close >= Center

Close > Open
```

То есть:

```text
pullback
→ wick touches center
→ body holds above
→ bullish close
```

### SHORT

```text
HTF = BEAR

High >= Center

Open <= Center

Close <= Center

Close < Open
```

---

# 13. Local trend

Начальная прозрачная модель:

### BULL

```text
Center[t] > Center[t-1]
AND
Close >= Center
```

### BEAR

```text
Center[t] < Center[t-1]
AND
Close <= Center
```

Иначе:

```text
NEUTRAL
```

---

# 14. Local POC

В live POC строить **не из OHLCV approximation**, если доступны trades.

Использовать:

```text
TradePrice
TradeQuantity
```

Разбить price range на bins:

```text
VolumeByPrice[bin] += trade quantity/notional
```

POC:

```text
price bin with maximum traded volume
```

Использования:

- confluence;
- reaction level;
- TP candidate;
- acceptance/rejection context.

POC сам по себе не является сигналом.

---

# 15. Order Blocks

Независимая approximation.

Bullish OB:

```text
последняя bearish candle
перед
bullish displacement >= ATR × ImpulseMultiplier
```

Bearish зеркально.

Zone:

```text
BODY
или
FULL_CANDLE
```

Block invalidation:

```text
body close through opposite boundary
```

Дополнительно:

```text
MaxLifetimeBars
MaxBlocksPerDirection
```

OB работает как confluence.

Не является обязательным условием V1.

---

# 16. Сохранённые экспериментальные setup families

Полная история обсуждений сохраняется в specification, но они **не должны одновременно запускаться в первом live**.

## SETUP-03 — Breakout Continuation

```text
range/level
→ confirmed breakout
→ volume acceleration
→ aggression confirms
→ continuation
```

---

## SETUP-04 — Stop Sweep + Reclaim

```text
local extreme swept
→ price returns through level
→ close confirms reclaim
→ aggression changes
```

---

## SETUP-05 — Structural Retest / Trend Zone

Сохраняет идеи прежнего Trend Zone Bot:

```text
structure
→ retest zone
→ active edge
→ EMA/VWAP/trendline alignment
→ structural SL
```

Исторически использовались:

```text
30m / 1h / 4h
EMA20 / EMA50 / EMA200
ATR14
VWAP
```

Эта стратегия должна тестироваться как отдельная hypothesis.

---

# 17. SETUP-06 — Low-Cap Pump Exhaustion Short

Сохраняется предыдущая Short Bot v2 гипотеза.

Это **отдельный profile**, а не часть ATR-channel entry.

Исторические research seeds:

```text
FDV < $50M
age < 6 months
daily turnover > $1M
drawdown from ATH >= 70%
spread < 0.5%

pump:
ret_15m >= 8%
OR ret_1h >= 12%
OR ret_4h >= 25%
```

плюс stretch:

```text
dist_to_vwap >= 8%
OR
volume zscore >= 1.5
OR
range/ATR >= 2
```

Также обсуждалось:

```text
second wave protection
failed continuation
no new high
lower high
breakdown
failed reclaim
```

Эти значения считаются **legacy research seeds**, а не production defaults.

---

# 18. SETUP-07 — Exhaustion Sniper

От прежнего Short Bot v2 сохраняются гипотезы:

```text
1m volume climax
upper wick
OI contraction
bearish close
```

Исторически рассматривались seeds:

```text
1m volume > 5x 30m average
upper wick > 50%
OI change 5m < -2%
close < open
```

Но этот профиль должен проходить отдельный OOS validation.

---

# 19. SqueezeGuard concepts

От прежнего SqueezeGuard сохраняются идеи:

```text
risk regime
squeeze score
quarantine
lower high
breakdown
failed reclaim
distribution confirmation
```

Главный смысл:

> При признаках потенциального squeeze бот должен не «угадывать вершину», а требовать structural confirmation либо блокировать сделку.

---

# 20. Lane A / Lane B / Lane C

Это финальная центральная архитектура стратегии.

---

# 21. Lane A — Price / Structure

Lane A отвечает:

> **Есть ли вообще структурный торговый setup?**

### Inputs

```text
OHLCV
HTF bars
ATR
MA
pivots
dynamic levels
channel
RVOL
VWAP
POC
order blocks
local structure
```

### Output

```text
NO_SETUP
FORMING
READY
INVALIDATED
```

Lane A **не отправляет order**.

Основные V1 setups:

```text
BOUNDARY_REJECTION
CENTER_RETEST
```

---

# 22. Lane B — Derivatives / Behaviour

Lane B отвечает:

> **Подтверждает ли рынок деривативов развитие setup?**

Inputs:

```text
Open Interest
OI delta
funding
liquidations
volume
failed continuation
price/OI behaviour
POC interaction
second wave
```

Output:

```text
CONFIRM
NEUTRAL
VETO
INSUFFICIENT_DATA
```

Важно:

```text
OI rises
```

или:

```text
OI falls
```

само по себе никогда не является сигналом.

---

# 23. Lane C — Microstructure / Heatmap

Lane C анализирует:

```text
L2 orderbook
depth
public trades
book history
```

Heatmap — это **визуализация history**, а не отдельная data source.

Сохраняем временную карту:

```text
timestamp
price level
bid quantity
ask quantity
adds
cancels/removals
executed volume
distance to mid
```

---

# 24. Lane C features

Минимально:

```text
spread

top-N bid depth
top-N ask depth

book imbalance

aggressive buy volume
aggressive sell volume

aggressor imbalance

wall persistence

wall replenishment

wall withdrawal

wall migration

pull-before-touch

liquidity added rate

liquidity removed rate

bid withdrawal

ask withdrawal

bid replenishment

ask replenishment

price response per aggressive volume

absorption

failed breakout

failed reclaim

liquidity vacuum
```

---

# 25. Spoof-like behaviour

Нельзя делать:

```text
big wall = spoofing
```

Можно вычислять:

```text
large displayed liquidity
→ persists/moves
→ disappears before touch
→ repeats
```

Feature называется:

```text
SPOOF_LIKE_PATTERN
```

а не доказанным spoofing.

---

# 26. Главный принцип Lane C

**Одна большая wall не является торговым сигналом.**

V1 Lane C:

```text
CONFIRM
NEUTRAL
VETO
INSUFFICIENT_DATA
```

Он не получает автономного права:

```text
C → ORDER
```

пока отдельное исследование не докажет edge.

---

# 27. Как Lane объединяются

V1 не использовать непрозрачный общий score вида:

```text
A=25
B=17
C=32
total=74 → buy
```

Начать с rule-based decision table.

Например:

```text
Lane A = READY  REQUIRED

Lane B =
CONFIRM | NEUTRAL
unless profile says REQUIRED

Lane C =
CONFIRM | NEUTRAL
unless profile says REQUIRED

any VETO
→ BLOCK
```

Решение:

```text
ALLOW
BLOCK
SHADOW
```

Причины всегда сохраняются.

---

# 28. Event lifecycle

Самая важная часть системы.

Один snapshot рынка **никогда** не должен сразу становиться сделкой.

Per-setup state machine:

```text
OBSERVING
    ↓
CONTEXT_READY
    ↓
SETUP_FORMING
    ↓
SETUP_CONFIRMED
    ↓
CONFIRMATION_WINDOW
    ↓
ENTRY_ARMED
    ↓
ORDER_PENDING
    ↓
POSITION_OPEN
    ↓
EXIT_PENDING
    ↓
COOLDOWN
    ↓
TERMINAL
```

Дополнительно:

```text
INVALIDATED
SUSPENDED
```

---

# 29. OBSERVING

Instrument находится в мониторинге.

Храним:

```text
market context
recent bars
ATR
volume baselines
distance to levels
```

Переход:

```text
context sufficiently initialized
→ CONTEXT_READY
```

---

# 30. SETUP_FORMING

Например:

```text
price approaches LowerBand
```

или:

```text
price begins breakout
```

В этот момент сделка ещё невозможна.

Сохраняем:

```text
SetupId
AnchorTime
AnchorPrice
AnchorLevel
HighSinceAnchor
LowSinceAnchor
ContextVersion
```

---

# 31. SETUP_CONFIRMED

Только после завершённого подтверждающего события.

Например:

```text
touch lower
+
confirmed close above lower
```

или:

```text
sweep
+
confirmed reclaim
```

---

# 32. CONFIRMATION_WINDOW

Здесь подключаются:

```text
Lane B
Lane C
volume
flow
```

Именно это предотвращает:

```text
one candle
→ immediate blind entry
```

---

# 33. ENTRY_ARMED

Создаётся immutable:

```text
ExecutionPlan
```

с:

```text
SetupId
Direction
EntryReference
MaxEntryPrice/MinEntryPrice
Stop
TP1
TP2
Qty
Expiry
RiskBudget
```

Если рынок изменился:

```text
plan invalidated
```

а не бесконечно переписывается вслед за ценой.

---

# 34. Setup identity

Каждый setup имеет уникальный fingerprint:

```text
Instrument
SetupType
Direction
StructuralAnchor
AnchorTime
ContextVersion
```

Повторный event не создаёт новый order.

Новый setup разрешён только после:

```text
terminal state
+
structure reset
```

---

# 35. Global runtime state

Отдельно существует system state:

```text
BOOTING
RECONCILING
READY
DEGRADED
HALTED
```

Новые позиции разрешены только:

```text
READY
```

---

# 36. Market universe

Финальная архитектурная capacity:

```text
UniverseSize             = max 250
CandidatePoolSize        = 50
RealtimeMonitorPoolSize  = 20
MaxActiveSetups          = 8
```

Это **не alpha-параметры**, а resource architecture.

---

# 37. Universe level

Universe состоит из подходящих:

```text
Binance USD-M perpetuals
status = tradable
valid instrument filters
acceptable base liquidity
```

Если инструментов больше 250:

ранжируем по:

```text
turnover
spread quality
liquidity
trading history
```

---

# 38. Candidate pool

50 инструментов.

Cheap features:

```text
relative volume
ATR expansion
price acceleration
distance to dynamic level
distance to channel
turnover
spread
OI anomaly
```

---

# 39. Realtime monitor

20 инструментов получают:

```text
trades
L2
higher-frequency state
```

Promotion происходит, если:

```text
setup proximity increases
volume anomaly
pump starts
price approaches boundary
price approaches structural level
```

---

# 40. Active setups

Максимум:

```text
8
```

Active setup не выталкивается из pool простым изменением ranking.

Он получает reservation до:

```text
INVALIDATED
TIMEOUT
TRADE CLOSED
```

---

# 41. Market data architecture

| Data | Получение | Level |
|---|---|---|
| Instrument metadata | REST | Universe |
| Server time | REST | System |
| All-market ticker | WS/periodic REST fallback | Universe |
| OHLCV | WS + local aggregation | Candidate |
| Trades | WS | Realtime |
| L2 depth | WS + REST snapshot | Realtime |
| Mark price | WS | Candidate/positions |
| Funding | mark-price stream / REST history | Candidate |
| OI | REST | Candidate |
| Liquidations | WS | Candidate/Realtime |
| Account/orders | private stream | Execution |
| Positions | private stream + REST reconciliation | Execution |
| FDV / market cap | external, optional | Pump profile |
| Token age | external, optional | Pump profile |
| Social | optional research | Experimental |

Binance документирует правильное восстановление локального order book как `WS buffer → REST snapshot → sequence alignment → apply deltas`. [Разработчики Binance](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/How-to-manage-a-local-order-book-correctly)

---

# 42. Adaptive frequencies

```text
Universe refresh        15 min
Universe ranking         1 min
Candidate rebalance      5 min

OI candidate             60 sec
OI hot                    30 sec

bars                    event-driven
trades                  realtime
L2                      realtime

Lane C features          250ms–1s aggregation

account/position         private WS realtime
reconciliation           startup/reconnect/periodic
health heartbeat         ~10 sec
```

Не должно быть одного polling timer для всего.

---

# 43. Local orderbook

Book reconstruction должен строго соблюдать Binance sequence rules.

При sequence gap:

```text
BookState = INVALID
```

затем:

```text
stop using Lane C
discard local book
fetch snapshot
buffer WS
rebuild
```

Если Lane C является required для текущего profile:

```text
new entry = BLOCK
```

---

# 44. Concurrency architecture

Внутренний transport:

```text
System.Threading.Channels
```

с bounded capacities.

Поток:

```text
WS readers
     ↓
bounded ingestion
     ↓
normalizer
     ↓
partition by instrument
     ↓
single consumer per partition/instrument
     ↓
FeatureEngine
     ↓
SetupLifecycle
```

Стандартные .NET Channels поддерживают bounded channels и backpressure. [System.Threading.Channels documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)

---

# 45. Determinism

Для одного инструмента events должны обрабатываться последовательно.

Не допускается:

```text
Trade #1
Depth #2
Trade #3
```

обрабатываемые в случайном async порядке.

Каждое internal event:

```text
EventId
InstrumentId
EventType
SourceTimestamp
ReceiveTimestamp
Sequence
Payload
```

Replay использует те же events.

---

# 46. Time

Core:

```text
UTC only
DateTimeOffset
TimeProvider
```

Запрещено:

```text
DateTime.Now
```

в торговой логике.

---

# 47. Monetary types

Для:

```text
Price
Quantity
Money
Notional
PnL
```

использовать:

```text
decimal
```

через value objects.

`double` разрешён для:

```text
z-score
normalized statistical feature
correlation
```

Если profiling позднее потребует большей скорости:

```text
price ticks = long
quantity steps = long
```

можно спрятать внутри value objects.

---

# 48. Technology projects

Финальная solution:

```text
TradingBot.slnx
```

Проекты:

```text
TradingBot.Core
TradingBot.Exchange.Binance
TradingBot.Infrastructure
TradingBot.Backtesting
TradingBot.App
```

---

# 49. Repository layout

```text
/
├── .github/
│   └── workflows/
│       └── ci.yml
│
├── src/
│   ├── TradingBot.Core/
│   ├── TradingBot.Exchange.Binance/
│   ├── TradingBot.Infrastructure/
│   ├── TradingBot.Backtesting/
│   └── TradingBot.App/
│
├── tests/
│   ├── TradingBot.Core.Tests/
│   ├── TradingBot.Exchange.Binance.Tests/
│   ├── TradingBot.Infrastructure.Tests/
│   ├── TradingBot.IntegrationTests/
│   └── TradingBot.Replay.Tests/
│
├── tools/
│   └── TradingBot.DataTool/
│
├── config/
│   ├── strategy/
│   ├── risk/
│   └── universe/
│
├── docs/
│   ├── architecture.md
│   ├── strategy-spec.md
│   ├── state-machine.md
│   ├── market-data.md
│   ├── risk-model.md
│   ├── execution-model.md
│   ├── references.md
│   ├── operations.md
│   └── adr/
│
├── deploy/
│   └── systemd/
│
├── scripts/
│   ├── verify.sh
│   ├── bootstrap-vps.sh
│   └── backup.sh
│
├── AGENTS.md
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── .editorconfig
├── .gitignore
├── README.md
└── TradingBot.slnx
```

---

# 50. Dependency boundaries

```text
TradingBot.Core
```

может использовать только BCL.

Core запрещено знать про:

```text
Binance
SQLite
HTTP
Telegram
Parquet
filesystem
```

---

## Exchange.Binance

Знает:

```text
REST
WS
authentication
rate limits
DTO
book reconstruction
```

Не знает:

```text
ATR
Setup
Lane
Risk policy
```

---

## Infrastructure

Знает:

```text
SQLite
Parquet
configuration
Telegram
logging
```

---

## Backtesting

Знает:

```text
Core
HistoricalEventSource
SimulatedExecution
ReplayClock
```

Не имеет собственной стратегии.

---

## App

Только composition root:

```text
DI
Host
mode
startup
shutdown
```

---

# 51. Dependencies

Минимальный stack:

| Dependency | Для чего |
|---|---|
| .NET 10 | runtime |
| Microsoft.Extensions.Hosting | process lifecycle |
| HttpClient/IHttpClientFactory | REST |
| ClientWebSocket | WS |
| Microsoft.Extensions.Http.Resilience | GET resilience |
| System.Threading.Channels | event pipelines |
| System.Text.Json | serialization |
| Microsoft.Data.Sqlite | relational persistence |
| Parquet.Net | high-frequency/replay data |
| Microsoft.Extensions.Logging | logging |
| System.Diagnostics.Metrics | metrics |
| xUnit v3 | tests |

EF Core:

```text
NO V1
```

Dapper:

```text
NO V1
```

Serilog:

```text
NO V1
```

Polly directly:

```text
NO unless Microsoft resilience proves insufficient
```

---

# 52. Configuration

Разделить:

```text
InfrastructureConfig
ExchangeConfig
UniverseConfig
StrategyConfig
RiskConfig
ExecutionConfig
```

Secrets только environment.

Пример environment names:

```text
TRADINGBOT__EXCHANGE__APIKEY
TRADINGBOT__EXCHANGE__APISECRET
TRADINGBOT__TELEGRAM__TOKEN
```

Не хранить secrets:

```text
Git
appsettings
logs
Telegram
database
```

---

# 53. Strategy configuration versioning

Каждая config:

```text
canonical JSON
→ SHA256
→ ConfigurationVersion
```

Каждый `StrategyRun` хранит:

```text
strategy git commit
configuration hash
runtime mode
start/end
```

Hot reload strategy config:

```text
DISABLED V1
```

Изменение стратегии:

```text
new StrategyRun
```

---

# 54. Runtime modes

```text
BACKTEST
REPLAY
SHADOW
PAPER
LIVE_LIMITED
LIVE
```

Запуск live не должен происходить через изменение одной случайной boolean.

Нужен явный:

```text
RuntimeMode
```

---

# 55. Risk Engine

Risk является отдельным модулем.

Он не знает, почему стратегия хочет LONG/SHORT.

Проверяет:

```text
account state
position state
market data health
risk per trade
daily loss
consecutive losses
portfolio exposure
margin
leverage
spread
depth
volatility
position count
kill switch
```

---

# 56. Position sizing

Для linear perp:

```text
RiskBudget =
Equity × RiskFraction
```

Не использовать ошибочную формулу:

```text
notional = risk / stop_distance
```

если `stop_distance` выражен в абсолютной цене.

Правильно:

```text
LossPerUnit =
abs(Entry - Stop)
+ fees_per_unit
+ estimated_slippage_per_unit
```

```text
RawQty =
RiskBudget / LossPerUnit
```

затем:

```text
lot rounding
tick rules
min qty
min notional
max notional
margin cap
```

---

# 57. Limited-live initial risk seeds

Это не final optimized parameters.

Для первой ограниченной live-фазы:

```text
RiskPerTrade         = 0.25% equity
MaxPositions         = 1
MaxDailyLoss         = 1%
MaxLeverage          = 5x hard ceiling
MarginMode           = ISOLATED
PositionMode         = ONE-WAY
Martingale           = FORBIDDEN
Loss averaging       = FORBIDDEN
```

После статистики значения можно пересматривать отдельно.

---

# 58. Stop loss

Stop обязателен до отправки entry plan.

Boundary LONG:

```text
below lower band
or
below signal low
or
ATR structural stop
```

Boundary SHORT зеркально.

Метод является config enum.

Stop нельзя:

```text
widen after entry
```

---

# 59. Take profit

## Boundary entry

LONG:

```text
TP1 = center or validated POC
TP2 = upper band / structural target
```

SHORT зеркально.

Initial management hypothesis:

```text
TP1 fills
→ close 50%
→ move remaining protective stop to breakeven
```

## Center retest

Center не может быть TP1, потому что entry уже возле него.

Поэтому:

```text
TP1 = 1R or nearest valid POC/structure
TP2 = opposite ATR boundary
```

---

# 60. Trailing stop

По умолчанию:

```text
OFF
```

Не включать одновременно с BE/TP logic без отдельного исследования.

---

# 61. Execution Engine

Execution получает только:

```text
RiskApprovedExecutionPlan
```

Не signal.

---

# 62. Entry order

Для scalping V1 предпочтительный candidate:

```text
MARKETABLE LIMIT IOC
```

с:

```text
MaximumAcceptedSlippage
PlanExpiry
```

Если не исполнен:

```text
не конвертировать автоматически в market
```

Нужен новый execution decision.

---

# 63. Idempotency

ClientOrderId должен детерминированно включать:

```text
StrategyRun
SetupId
OrderRole
Attempt
```

Например:

```text
ENTRY
STOP
TP1
TP2
EMERGENCY
```

Одинаковый retry не может создать duplicate exposure.

---

# 64. Ambiguous execution

Binance прямо указывает, что при некоторых timeout/5xx execution status может быть **UNKNOWN**, и перед повторной отправкой нужно проверить WebSocket/order query. [Разработчики Binance](https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info)

Следовательно:

```text
POST timeout
≠
order failed
```

Переход:

```text
SUBMITTING
→ UNKNOWN
→ RECONCILING
```

Blind retry запрещён.

---

# 65. Partial fill

Если entry исполнен частично:

```text
filled exposure
```

должен быть защищён stop.

Не ждать 100% entry fill, оставляя partial position без protection.

---

# 66. Reduce-only

Закрывающие orders:

```text
reduce-only
```

где это поддерживается режимом account/venue.

---

# 67. Reconciliation

При startup:

```text
load local checkpoint
↓
connect public/private streams
↓
server time
↓
fetch balances
↓
fetch open positions
↓
fetch open orders
↓
fetch recent fills/orders
↓
compare local state
↓
repair
↓
verify SL
↓
READY
```

До:

```text
READY
```

новых trades нет.

---

# 68. Источник истины

По открытым positions/orders:

```text
EXCHANGE
```

а не SQLite.

SQLite является:

```text
journal
audit
checkpoint
research store
```

---

# 69. Binance rate limits

Система обязана читать response headers и exchange-info limits.

Binance указывает, что `429` требует backoff, а продолжение запросов после limit violations может привести к IP ban `418`. [Разработчики Binance](https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info)

Нельзя:

```text
while(error) retry immediately
```

---

# 70. Database

V1:

```text
SQLite
WAL
single writer
busy_timeout
bounded write channel
```

---

# 71. Main tables

```text
instrument
configuration_version
strategy_run

setup
setup_transition

signal
signal_rejection

risk_decision

order
order_event
fill

position
position_event
trade

system_event
health_event

market_capture_segment
```

---

# 72. NO TRADE storage

Критически важно сохранять:

```text
NO_TRADE
```

с reason codes.

Например:

```text
HTF_MISMATCH
CHANNEL_TOO_NARROW
SPREAD_TOO_WIDE
OI_STALE
BOOK_INVALID
LANE_C_VETO
RISK_DAILY_LIMIT
DUPLICATE_SETUP
SIGNAL_EXPIRED
```

Иначе нельзя будет оценить эффективность фильтров.

---

# 73. High-frequency storage

Не писать каждую L2 delta строкой в SQLite.

Использовать:

```text
RAM ring buffer
+
compressed Parquet
```

Для HOT instrument:

```text
~15 min pre-event ring
```

Когда возникает setup:

```text
persist pre-event
+
setup lifecycle
+
post-event
```

---

# 74. Backtest / Replay architecture

Одна и та же:

```text
FeatureEngine
LaneA
LaneB
LaneC
SetupLifecycle
DecisionEngine
RiskMath
```

используется в:

```text
LIVE
SHADOW
PAPER
REPLAY
BACKTEST
```

Нельзя создавать:

```text
BacktestStrategy
LiveStrategy
```

с разной логикой.

---

# 75. Что можно backtest по OHLCV

Можно:

```text
MA
ATR
dynamic pivots
HTF context
channel
boundary rejection
center retest
simplified order block
bar volume
basic risk/TP/SL
```

---

# 76. Что нельзя честно восстановить из OHLCV

Нельзя:

```text
intrabar sequence
spread
actual fills
queue position
L2 walls
wall pulling
absorption
trade aggression
failed micro breakout
true POC
liquidation sequence
short-term OI sequence
```

Для этого требуется:

```text
trades
L2
OI history
liquidations
```

---

# 77. No lookahead

Правила:

```text
closed bar only
confirmed pivots only
HTF close confirmation only
no future extrema
no backfilled historical signal movement
```

В replay события поступают строго по timestamp.

---

# 78. Trading edge validation

Engineering correctness ≠ profitable strategy.

Каждая гипотеза тестируется отдельно.

Порядок:

```text
H1 Boundary Rejection

H2 Center Retest

H1 + Lane B

H1 + Lane C

H2 + Lane B

H2 + Lane C
```

а не сразу:

```text
HTF + ATR + OI + POC + OB + funding + heatmap
```

---

# 79. Correlated features

Особенно вероятно дублирование:

```text
RVOL ↔ trade rate

ATR expansion ↔ range expansion

bid/ask depth ratio ↔ book imbalance

MA trend ↔ center slope

failed continuation ↔ low price-response efficiency
```

Количество filters не равно количеству независимых источников edge.

---

# 80. Anti-overfitting protocol

Data split:

```text
TRAIN/DEVELOPMENT
VALIDATION
FROZEN OOS
```

Дополнительно:

```text
walk-forward
regime split
instrument split
fee stress
slippage stress
latency stress
parameter perturbation
```

После просмотра OOS нельзя:

```text
изменить threshold
→ повторно назвать тот же период OOS
```

---

# 81. Survivorship bias

Historical universe должен учитывать:

```text
delisted instruments
old listings
actual listing dates
```

а не сегодняшние surviving pairs.

---

# 82. Selection bias

Universe rules должны фиксироваться **до** evaluation.

Нельзя после backtest:

```text
убрать монеты, на которых стратегия проиграла
```

без нового OOS experiment.

---

# 83. Observability

Минимум:

```text
structured JSON logging
metrics
heartbeat
health state
Telegram alerts
disk guard
DB health
WS lag
data staleness
reconciliation state
```

---

# 84. Metrics

```text
events_received_total
events_rejected_total

ws_reconnect_total
ws_age_ms

rest_latency_ms
rate_limit_usage

channel_depth

candidate_count
hot_count
active_setups

lane_a_ready_total
lane_b_veto_total
lane_c_veto_total

signals_allowed
signals_blocked

orders_submitted
orders_rejected
orders_unknown

positions_open

db_write_latency
disk_free_bytes
```

---

# 85. Fail-safe behaviour

| Problem | Action |
|---|---|
| REST unavailable | block operations requiring REST |
| Public WS disconnected | block affected setups |
| Private WS disconnected | no new entries |
| WS stale | no new entries |
| Book sequence gap | invalidate book + rebuild |
| OI stale | block if profile requires OI |
| DB temporary lock | bounded retry |
| DB unavailable too long | block entries |
| Disk almost full | block entries + alert |
| Clock drift | block signed execution |
| Order rejected | terminate/re-evaluate attempt |
| Order status unknown | reconciliation |
| Local position ≠ exchange | exchange wins |
| Position with missing SL | emergency protection |
| TP missing but SL valid | recreate TP |
| SL cannot be restored | emergency flatten policy |
| API 429 | backoff |
| API 418 | halt API requests + alert |
| VPS reboot | reconcile before READY |
| Process crash | systemd restart → reconcile |
| Unknown system state | `HALTED/DEGRADED`, no entry |

Основной принцип:

```text
UNCERTAINTY
→ NO NEW RISK
```

---

# 86. VPS deployment

Filesystem:

```text
/opt/trading-bot/        app
/etc/trading-bot/        config
/etc/trading-bot/bot.env secrets
/var/lib/trading-bot/    SQLite/Parquet
```

`bot.env`:

```text
0600
```

---

# 87. systemd

Service:

```text
Restart=on-failure
```

Но auto restart:

```text
≠
auto trading enabled
```

Каждый restart проходит:

```text
BOOTING
→ RECONCILING
→ READY
```

---

# 88. Telegram

Telegram используется только для:

```text
setup notifications
orders
fills
risk blocks
critical health events
daily summary
```

Telegram **не является state storage**.

Команда в Telegram не должна молча менять critical risk settings.

---

# 89. CI

С первого commit:

```text
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build -c Release --warnaserror
dotnet test -c Release
```

---

# 90. Build policy

`Directory.Build.props`:

```text
net10.0
Nullable=enable
ImplicitUsings=enable
TreatWarningsAsErrors=true
Deterministic=true
latest recommended analyzers
```

---

# 91. AGENTS.md — обязательные правила Codex

Codex обязан соблюдать:

```text
Core does not depend on Infrastructure.

Exchange DTO cannot enter Core.

Strategy cannot call HTTP.

Risk cannot be bypassed by Strategy.

All async long-running operations accept CancellationToken.

No fire-and-forget.

No silent catch.

No infinite retry.

No unbounded Channel.

No hardcoded secret.

No secret logging.

No DateTime.Now in Core.

No double for money without documented reason.

No strategy decision without reason code.

No exchange API implementation without PRIMARY reference.

No live capability without tests.
```

---

# 92. ADR

Минимум:

```text
ADR-001 Technology Stack
ADR-002 Exchange Integration
ADR-003 Event/Market Data Transport
ADR-004 Persistence
ADR-005 Strategy State Machine
ADR-006 Shared Backtest/Live Core
ADR-007 Orderbook Storage
ADR-008 VPS Deployment
ADR-009 Execution Idempotency
ADR-010 Fail-Closed Policy
```

---

# 93. Roadmap

## Phase 0 — Specification Freeze

Создать:

```text
architecture.md
strategy-spec.md
state-machine.md
market-data.md
risk-model.md
execution-model.md
references.md
operations.md
ADRs
```

DoD:

все `UNKNOWN` явно отмечены.

---

## Phase 1 — Repository Bootstrap

Создать весь solution/repository skeleton.

DoD:

```text
restore green
format green
build green
tests green
CI green
```

Без стратегии.

---

## Phase 2 — Core Contracts

Создать domain types:

```text
InstrumentId
Price
Quantity
MarketEvent
Trade
BookDelta
Bar
FundingUpdate
OIUpdate
Setup
Decision
RiskDecision
OrderIntent
Position
```

Tests first.

---

## Phase 3 — Persistence + Replay Foundation

До стратегии.

Создать:

```text
SQLite migrations
journal
StrategyRun
ConfigVersion
replay envelope
```

Artificial event stream должен сохраняться и воспроизводиться.

---

## Phase 4 — Binance Public Market Data

Реализовать:

```text
exchangeInfo
serverTime
tickers
bars
trades
mark price
funding
OI
liquidations
depth
local orderbook
```

---

## Phase 5 — Universe Manager

Реализовать:

```text
250
↓
50
↓
20
↓
8
```

---

## Phase 6 — Feature Engine

```text
ATR
MA
pivots
RVOL
VWAP
POC
dynamic levels
channel
```

---

## Phase 7 — Lane A V1

Только:

```text
BoundaryRejection
CenterRetest
```

---

## Phase 8 — Setup Lifecycle

Полный state machine.

Критический DoD:

```text
duplicate event cannot create duplicate entry
```

---

## Phase 9 — Replay + OHLCV Backtest

Проверить Lane A без microstructure.

---

## Phase 10 — Lane B

Добавить:

```text
OI
funding
liquidations
failed continuation
```

---

## Phase 11 — Risk Engine

Полностью независимо от Strategy.

---

## Phase 12 — Private API / Execution

Добавить:

```text
orders
fills
positions
private stream
reconciliation
native stop
TP
partial fill
unknown order recovery
```

Без live-money activation.

---

## Phase 13 — Lane C

Только теперь:

```text
L2 history
trades
flow
absorption
walls
replenishment
withdrawal
```

В shadow.

---

## Phase 14 — Full Historical Replay

L2/trades/OI datasets.

---

## Phase 15 — 24/7 Shadow VPS

Реальный рынок.

```text
signals
risk decisions
hypothetical orders
```

Но:

```text
0 real orders
```

---

## Phase 16 — Paper/Test Environment

Полный execution lifecycle.

---

## Phase 17 — Limited Live

Seeds:

```text
0.25% risk
1 position
isolated
```

---

## Phase 18 — Production

Только после live execution dataset.

---

# 94. Testing plan

Обязательные test categories:

```text
Unit
Property/invariant
State-machine
Serialization
Exchange fixtures
WS reconnect
Order-book recovery
Rate-limit
Execution
Idempotency
Recovery
Persistence
Replay
Backtest
Failure injection
Shadow
Paper
```

---

# 95. Критические тесты

Обязательно:

```text
same event twice
out-of-order event
missing event
book sequence gap
stale trade stream
stale OI
stale private stream
WS reconnect

order accepted
rejected
partial fill
complete fill
cancel
cancel reject

POST timeout
UNKNOWN order
duplicate retry

restart with open order
restart with open position
restart with missing SL

SQLite busy
SQLite restart
disk full simulation

daily risk hit
portfolio risk hit
kill switch
```

---

# 96. Determinism test

Главный regression test:

```text
HistoricalEvents
→ Run #1
→ Decisions A

same HistoricalEvents
→ Run #2
→ Decisions B

A == B
```

Включая:

```text
SetupIds
state transitions
signals
risk decisions
execution plans
```

---

# 97. Reference Catalog — Strategy Sources

### REF-S01 — MikhailPrizrak

Публичное описание поведения.

[TradingView — MikhailPrizrak1](https://ru.tradingview.com/script/MogHlyUs-mikhailprizrak1/)

Trust:

```text
AUTHOR/PUBLIC_DESCRIPTION
```

Использовать для:

```text
dynamic levels
context
MA
visual behavioural reference
```

Не использовать для reverse engineering.

---

### REF-S02 — Prizrak Trend

[TradingView — Prizrak Trend1](https://ru.tradingview.com/script/NZEwELqs-prizrak-trend1/)

Использовать:

```text
ATR channel concept
boundary reaction
center retest
```

---

### REF-S03 — Top-down video

[YouTube — top-down system](https://youtu.be/ds2cCUWaMCA)

Trust:

```text
DEMONSTRATION
```

---

### REF-S04 — Prizrak Trend video

[YouTube — Prizrak Trend demonstration](https://youtu.be/GHNU2ptm42Y)

---

### REF-S05 — Probability Theory-like indicator video

Источник:

```text
USER PROVIDED VIDEO
URL = NOT PROVIDED
```

Использовать только как behavioural reference для `DynamicTrendState`.

---

### REF-S06 — Previously supplied scalping notes

```text
01_plan_scalping.md
02_market_state.md
03_futures.md
```

Source:

```text
USER PROVIDED
URL = NOT PROVIDED
```

---

# 98. Binance PRIMARY references

### REF-B01 — USD-M General Information

[Binance USD-M General Information](https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info)

Использовать:

```text
authentication
HTTP behaviour
timeouts
UNKNOWN execution
rate limits
timing
```

---

### REF-B02 — USD-M Market Data

[Binance USD-M Market Data API](https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data)

Использовать:

```text
exchangeInfo
depth
klines
trades
funding
OI
tickers
```

---

### REF-B03 — Exchange Information

[Binance USD-M Exchange Information](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Exchange-Information)

Использовать для:

```text
tick size
lot size
min notional
status
rate limits
```

---

### REF-B04 — WebSocket subscription

[Binance live WS subscribe/unsubscribe](https://developers.binance.com/en/docs/products/derivatives-trading-usds-futures/websocket-market-streams/Live-Subscribing-Unsubscribing-to-streams)

---

### REF-B05 — Diff Depth

[Binance Diff Book Depth Streams](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Diff-Book-Depth-Streams)

---

### REF-B06 — Local Order Book

[How to manage a Binance local order book correctly](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/How-to-manage-a-local-order-book-correctly)

**Critical PRIMARY reference.**

---

### REF-B07 — Aggregate Trades

[Binance Aggregate Trade Stream](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Aggregate-Trade-Streams)

---

### REF-B08 — Mark Price / Funding

[Binance Mark Price Stream](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Mark-Price-Stream)

---

### REF-B09 — Open Interest

[Binance Open Interest](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Open-Interest)

---

### REF-B10 — OI Statistics

[Binance Open Interest Statistics](https://developers.binance.com/docs/derivatives/usds-margined-futures/market-data/rest-api/Open-Interest-Statistics)

---

### REF-B11 — Liquidations

[Binance Liquidation Order Streams](https://developers.binance.com/docs/derivatives/usds-margined-futures/websocket-market-streams/Liquidation-Order-Streams)

---

### REF-B12 — Orders

[Binance USD-M New Order API](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/New-Order)

---

### REF-B13 — Query Order

[Binance USD-M Query Order](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Query-Order)

---

### REF-B14 — Cancel Order

[Binance USD-M Cancel Order](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Cancel-Order)

---

### REF-B15 — Account

[Binance Account Information V3](https://developers.binance.com/docs/derivatives/usds-margined-futures/account/rest-api/Account-Information-V3)

---

### REF-B16 — Position

[Binance Position Information V3](https://developers.binance.com/docs/derivatives/usds-margined-futures/account/rest-api/Position-Information-V3)

Если URL меняется/redirect — coding agent должен брать актуальную страницу из current API catalog.

---

### REF-B17 — Leverage

[Binance Change Initial Leverage](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Change-Initial-Leverage)

---

### REF-B18 — Margin Type

[Binance Change Margin Type](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Change-Margin-Type)

---

### REF-B19 — Position Mode

[Binance Change Position Mode](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/Change-Position-Mode)

---

### REF-B20 — Order updates

[Binance ORDER_TRADE_UPDATE](https://developers.binance.com/docs/derivatives/usds-margined-futures/user-data-streams/Event-Order-Update)

---

### REF-B21 — Position/account updates

[Binance ACCOUNT_UPDATE](https://developers.binance.com/docs/derivatives/usds-margined-futures/user-data-streams/Event-Balance-and-Position-Update)

---

### REF-B22 — Error codes

[Binance USD-M Error Codes](https://developers.binance.com/docs/derivatives/usds-margined-futures/error-code)

Особенно:

```text
-1006 UNKNOWN
-1007 TIMEOUT
-1008 throttled
-1021 timestamp
```

Binance прямо документирует `-1006/-1007` как unknown execution status. [Разработчики Binance](https://developers.binance.com/docs/derivatives/usds-margined-futures/error-code)

---

# 99. .NET PRIMARY references

### REF-N01

[.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy)

---

### REF-N02

[System.Threading.Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)

---

### REF-N03

[Microsoft HTTP Resilience](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience)

---

### REF-N04

[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)

---

### REF-N05

[.NET Options Pattern](https://learn.microsoft.com/en-us/dotnet/core/extensions/options)

---

### REF-N06

[.NET JSON Console Logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/console-log-formatter)

---

### REF-N07

[System.Diagnostics.Metrics](https://learn.microsoft.com/dotnet/api/system.diagnostics.metrics)

---

### REF-N08

[GitHub Actions for .NET](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)

---

### REF-N09

[xUnit v3 Getting Started](https://xunit.net/docs/getting-started/v3/getting-started)

---

# 100. Reference implementations

### REF-R01 — NautilusTrader

[NautilusTrader GitHub](https://github.com/nautechsystems/nautilus_trader)

Trust:

```text
EXAMPLE_ONLY
```

Использовать для:

```text
event-driven architecture
replay
simulation/live parity
reconciliation concepts
```

Не dependency.

---

### REF-R02 — Nautilus architecture

[NautilusTrader Architecture](https://github.com/nautechsystems/nautilus_trader/blob/develop/docs/concepts/architecture.md)

---

### REF-R03 — Nautilus docs

[NautilusTrader Documentation](https://nautilustrader.io/docs/)

---

### REF-R04 — Binance official .NET connector

[Binance connector-dotnet](https://github.com/binance/binance-connector-dotnet)

Trust:

```text
OFFICIAL / EXAMPLE_ONLY
```

Не использовать как Futures dependency из-за отсутствия `/fapi/*`. [GitHub](https://github.com/binance/binance-connector-dotnet)

---

### REF-R05 — Binance official Futures Python SDK

[Binance Futures Python SDK repository](https://github.com/binance/binance-connector-python)

Trust:

```text
OFFICIAL / EXAMPLE_ONLY
```

Использовать для понимания generated models/examples, но official API docs имеют больший приоритет.

---

# 101. Microstructure references

### REF-M01 — Order Flow Imbalance

Cont, Kukanov & Stoikov:

[The Price Impact of Order Book Events — arXiv](https://arxiv.org/abs/1011.6402)

Исследование показывает связь short-term price changes с order-flow imbalance и market depth. [arXiv](https://arxiv.org/abs/1011.6402)

Это **не доказательство edge на Binance crypto**, а математическая основа для исследования OFI.

---

### REF-M02 — Limit Order Books survey

[Limit Order Books — Oxford/ArXiv](https://arxiv.org/abs/1012.0349)

Использовать для terminology и market-microstructure context.

---

# 102. Legacy project references

Эти проекты не являются source of truth, но содержат предыдущие идеи.

### REF-L01

[Legacy short-telegram-bot](https://github.com/KirillNedoboy/short-telegram-bot)

Status:

```text
EXAMPLE_ONLY / VERIFY ACCESS
```

---

### REF-L02

[Legacy short-telegram-bot-lite](https://github.com/KirillNedoboy/short-telegram-bot-lite)

Использовать только как historical idea/reference.

Новый repository создаётся независимо.

---

# 103. Deferred exchange reference

Bybit был основой нескольких прежних ботов.

Он не входит в V1 implementation, но abstraction должна позволить добавить его позже.

[Official Bybit V5 API Documentation](https://bybit-exchange.github.io/docs/v5/intro)

Bybit V5 объединяет Spot/Derivatives/Options в общей API-модели. [Bybit Exchange](https://bybit-exchange.github.io/docs/v5/intro)

---

# 104. Reference priority

Codex обязан использовать источники в таком порядке:

```text
1. Official exchange API documentation
2. Official SDK documentation
3. Official source repository
4. Protocol specification
5. Peer-reviewed/original research
6. Trusted reference implementation
7. Blog/article
8. Random GitHub example
```

Если GitHub example противоречит current Binance docs:

```text
Binance docs win
```

---

# 105. Freshness policy

Перед реализацией endpoint:

```text
verify URL
verify current API product
verify endpoint exists
verify not deprecated
verify parameters
verify response fields
verify rate weight
verify error behaviour
verify authentication
verify latest changelog
```

Это особенно важно, потому что Binance API и SDK продолжают меняться в 2026 году.

---

# 106. Что пока остаётся UNKNOWN

Нельзя фиксировать без исследования:

| Параметр | Статус |
|---|---|
| Final ATR period | NEEDS_VALIDATION |
| Final channel multiplier | NEEDS_VALIDATION |
| Final MA lengths | research seed 21/55 |
| Exact HTF combination | NEEDS_VALIDATION |
| Max holding time | NEEDS_VALIDATION |
| Lane B required vs optional per setup | needs experiment |
| Lane C veto thresholds | needs dataset |
| Funding thresholds | UNKNOWN |
| OI thresholds | UNKNOWN |
| Order-block multiplier | UNKNOWN |
| Exact POC window | UNKNOWN |
| FDV provider | NOT SELECTED |
| Token-age provider | NOT SELECTED |
| Social provider | NOT SELECTED |
| Production risk above limited-live | UNKNOWN |

Не придумывать эти значения в Codex.

---

# 107. Финальная стратегия V1 в одной схеме

```text
ALL ELIGIBLE USD-M PERPS
           │
           ▼
     UNIVERSE ≤ 250
           │
           ▼
     CHEAP SCREENER
           │
           ▼
     50 CANDIDATES
           │
           ▼
   HTF MARKET CONTEXT
  MA + dynamic levels
           │
           ▼
     LOCAL ATR CHANNEL
           │
      ┌────┴────┐
      │         │
 Boundary    Center
Rejection    Retest
      │         │
      └────┬────┘
           │
        Lane A
           │
           ▼
      SETUP LIFECYCLE
           │
           ▼
        Lane B
 OI/Funding/Liquidations
           │
           ▼
        Lane C
 L2/Trades/Microstructure
           │
           ▼
     DECISION ENGINE
           │
   ALLOW / BLOCK / SHADOW
           │
           ▼
       RISK ENGINE
           │
           ▼
     EXECUTION PLAN
           │
           ▼
   BINANCE EXECUTION
           │
           ▼
  POSITION MANAGEMENT
           │
           ▼
 TP / STOP / TIME EXIT
           │
           ▼
        COOLDOWN
           │
           ▼
        OUTCOME
           │
           ▼
      REPLAY / RESEARCH
```

---

# 108. Первый запрос для Codex

После передачи этого документа следующий запрос должен быть только про bootstrap:

> **Создай новый greenfield repository согласно финальному ТЗ. Выполни Phase 0 и Phase 1: создай Git repository, .NET 10 solution, project boundaries, tests projects, Directory.Build.props, Directory.Packages.props, global.json, editorconfig, gitignore, CI, README, AGENTS.md, docs и ADR skeleton. Не реализовывай торговую стратегию, Binance API, Lane A/B/C или autoexecution. После создания выполни `dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -c Release --warnaserror`, `dotnet test -c Release`. Исправь все ошибки до зелёного состояния. В конце покажи дерево repository, package list, dependency graph и результаты validation commands.**

После этого следующий этап уже будет **Core Contracts**, а не дополнительное проектирование.

---

## Короткий итог

Финальная система — это **greenfield C#/.NET 10 production trading bot для Binance USD-M perpetuals**, работающий 24/7 на VPS. Его основная торговая модель — краткосрочный intraday/scalping: HTF задаёт направление, ATR/market structure формируют setup, Lane B анализирует derivatives, Lane C — order flow/L2, затем независимые Decision → Risk → Execution. Старые pump/pullback, Trend Zone, SqueezeGuard, stop-sweep и breakout идеи не потеряны: они сохранены как отдельные setup families и должны добавляться по одной после измеримого OOS-исследования, а не смешиваться в первоначальный сигнал.