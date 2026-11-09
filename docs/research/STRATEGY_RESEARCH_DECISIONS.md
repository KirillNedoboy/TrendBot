# TrendBot — решения по signal-only и исследованию стратегий

Дата: 2026-11-09. Датированная research decision запись, не approval исполнения/параметров и не результат проверки alpha. Полный аудит исходников: [architecture review](TRENDBOT_FINAL_ARCHITECTURE_REVIEW.md), [Pine audit](PRIZRAK_V21_ALGORITHM_AUDIT.md). Уровни `VERIFIED / SOURCE_CLAIM / INFERENCE / HYPOTHESIS / UNKNOWN` имеют значения из architecture review.

## 1. Источники и precedence

- **A:** фактический `additional-strategy-research-plan.md`, ревизия 2026-11-06, прочитан полностью; запрошенный вариант имени с `(4)` не найден. Хеш зафиксирован в review.
- **B:** `мои предложения..txt`, прочитаны все 210 строк; новая исследовательская матрица/дальняя архитектура, не датированное разрешение текущей торговли.
- **P:** `Prizrak_v2.1.zip:Prizrak_v2.1.pine`, все 1563 строки; исходный текст independent toolkit, не verified identity закрытого Prizrak. Вариант ZIP имени с `(1)` недоступен, audited SHA приведён отдельно.
- **R:** local workspace на HEAD `76ea2bac6a8f1baf25783fe9a0923fd47af03179` плюс сохранённые dirty edits. Код — источник истины реализованного; рабочая согласованная поправка — источник текущего scope; canonical source остаётся историческим неизменным baseline.

**VERIFIED decision:** ближайший продукт — native public 15m LONG/SHORT research signals с initial closed 1h context. Это уже принято 2026-11-06 (`PROJECT_SPEC:Approved current scope`, A:3,29–42), а не новое молчаливое решение аудитора. Новый пользовательский запрос подтверждает границу. Иного позднейшего авторизованного production TF в прочитанных локальных документах нет. Изображения/TV backtest не повышают статус evidence.

## 2. Decision register

| Вопрос | A: позиция и anchor | B: позиция и anchor | Фактический R / P | Достоверность | Решение | Последствия | Выбор владельца |
|---|---|---|---|---|---|---|---|
| 15m / 30m / 1h setup TF | 15m основной, 1h HTF; §3 | 30m первым исследовательским TF, 15m/1h сравнения; 114–126 | R `PROJECT_SPEC:9–17` уже 15m amendment; P:36 default HTF=1D, chart TF произволен | VERIFIED scope; оптимальность UNKNOWN | сохранить 15m/1h baseline; 30m/1h setup и 4h/1d context только отдельные frozen studies | нельзя перенести evidence/periods 5m на новый TF | нет для текущего TF; эксперимент требует preregistration, не «лучшего» TF заранее |
| Signal-only / autonomy | no OrderIntent/positions/reservations/private API; §1,4 | Portfolio Risk→Execution и autonomous target; 127–171,203–210 | R Program не содержит signal service; P strategy backtest optional | VERIFIED границы; future architecture SOURCE_CLAIM | autonomous executor — дальняя независимая ветка; из critical path сигналов исключить все order/risk/private gates | не запускать paper/demo/testnet как smoke signal service | нет разрешения торговли; отдельный будущий запрос обязателен |
| SETUP-01 / S1 | baseline Boundary; §1,7 | ATR touch→inside return; 13–17 | R `BoundaryRejectionSetupEvaluator:EvaluateDirection`; P boundary имеет иные gates | VERIFIED overlap, не identical | reuse SETUP-01, не новый evaluator с именем S1; отличающиеся условия только отдельная ablation/version | одна family identity для baseline, разные hypotheses не смешивать с V1 | нет для reuse; изменение правила требует отдельной регистрации |
| SETUP-02 / S4 | baseline Center; §1 | trend pullback/hold; 67–71 | R Center требует open-hold+candle+quality; P open-hold не требует, добавляет width | VERIFIED overlap, не identical | reuse SETUP-02; S4 не duplicate implementation | не импортировать Pine min-width/countertrend/defaults | нет для baseline |
| R1/R2 / S2/S3 | R1 failed breakout/reclaim; R2 breakout→retest; §7 | F2/F3 labels; 31–53 | R нет detectors обоих семейств; external Lane B failed-continuation не структурный evaluator | VERIFIED absence в проверенных source paths; edge HYPOTHESIS | R1↔S2, R2↔S3 — research identities; независимая последовательная state model с известным ДО события уровнем | ни скрытые mandatory filters V1, ни переименование Center; F2/F3 — ссылки B, не actual task IDs | да для frozen experiment scope; не блок baseline |
| S5 / Flow + reclaim | Flow не часть baseline, простые модели сначала; §7 | три волны и timing before/same/after/reclaim-without-Flow; 85–89,172–188 | P:332–348 три различных wave; pressure — OHLCV transform, не order flow; .NET wave отсутствует | VERIFIED formulas; value HYPOTHESIS | три отдельные Flow-only families и additive R1 timing ablations после baseline/R1 | методы/thresholds/normalization не объединять до независимого результата | preregistration каждой волны; не нужно для SR-01…06 |
| S6 compression | карантин; §7 | later F6; 103–107 | Core ATR/SD есть, compression→expansion FSM нет; P width gate не S6 | VERIFIED mechanism gap; value HYPOTHESIS | позднее против ordinary confirmed-breakout control | не считать каждый narrow channel новым edge | только для отдельного frozen study |
| OB / POC | optional, источники данных важны; §4,7 | additive S2/S3 features; 113 | P last-bar display и historical approximate POC различны; R exact-trade POC; OB missing | VERIFIED source differences; value UNKNOWN | ablation после price-only family, immutable provenance; display POC никогда historical label | OHLCV allocation не true volume-at-price; OB source bar не birth availability | источник/окно/версия нужны только для optional study |
| Lane B/C и ML | separate comparisons, не prerequisites; §1,7 | conditional candidate evaluation и later queue; 152,197–198 | R реализованы evidence kernels и synthetic fixtures, real coverage не доказана | VERIFIED code; edge UNKNOWN | keep optional research; missing optional input не подменять Neutral; ML после достаточных data/простых controls | не навязывать depth/OI/cost feed для первичного Lane A rule-readiness | нет для baseline; dataset/criteria для optional study |
| 5m canonical / новая поправка | §1,3: canonical не менять | multiple TF research | R canonical §4=5m; working spec amendment 2026-11-06=15m | VERIFIED | dated working-document precedence только для нового signal profile | не менять bytes/hash/исторические proofs; новый TF — новый evidence | нет: текущий scope уже согласован |
| CONFIRMED / VALIDATED / authorized | разные состояния; §4,6 | исследования ещё не validation; 115,202 | R evaluator Ready — правила; P A/B/C — confluence, `CONFIRMED` — return+filters | VERIFIED meanings; прибыль UNKNOWN | независимые ruleState, researchStatus, deliveryState, outcomeStatus; signal schema не содержит order authorization | ни «A», ни CONFIRMED не calibrated probability | нет для разделения состояний; calibration требует исследования |
| Regime detector / strategy router | не requirement baseline | сначала classify trend/range, затем выбирать family; 127–160 | P marketMode — label; R нет verified adaptive multi-strategy selector | VERIFIED absence evidence; HYPOTHESIS | не вводить router сейчас; будущий router сравнивать с unconditioned families на общей opportunity table | classifier errors, delayed regime и extra model selection учитываются | только после независимого frozen research |
| Reuse FrozenResearch/risk/paper | §6 отделяет signal study от promotion | 202 предлагает reuse existing components | R `ResearchFreeze:Create` требует risk/fill strings; FrozenOosEvaluator требует reconciliation/evidence; paper — orders/positions simulation | VERIFIED API constraints | reuse canonical hashing/replay patterns, не fake execution policy/reconciliation; отдельный signal-study report | не ослаблять PromotionReviewRecord и не запускать бумажные позиции | нет для границы; signal manifest реализуется отдельной задачей |
| Registration governance | SR items пока не TASKS; §5 | implementation queue без graph | R validator only phases 00–18; fixtures fixed counts/stale P17 text; 0 READY explicit blocker | VERIFIED validator/source + PS5 fixture failure | proposed P15-M03 signal branch и P01-M03 tooling prerequisite; patch в roadmap не применён | docs-only scope сохранён; не маскировать fixture failure изменением P17 wording | разрешить отдельную tooling-maintenance задачу перед registration |

Все новые исследовательские решения ниже имеют статус **PROPOSED / HYPOTHESIS**. Принята только уже разрешённая product boundary и способ не смешивать evidence.

## 3. Шесть кандидатов: новизна и цена

Оценки стоимости относительные инженерные, не время/доходность. «Низкая» — reuse имеющегося price OHLCV пути после SR; «высокая» — новые state/provenance и/или более детальные данные.

| Кандидат | Новизна относительно Core | Data / implementation cost | Leakage / timing risk | Очередь | Reject / inconclusive |
|---|---|---|---|---|---|
| S1 ATR Boundary Reversal | механизм SETUP-01 уже есть; B не задаёт точных HTF/trend/width/quality условий | низкая: native 15m/1h; средняя integration signal journal, не новый evaluator | current-band formula, pivot availability, TF seed и publication latency | baseline вместе с S4 | нет edge относительно controls после legal publication/costs → не VALIDATED; мало coverage → UNKNOWN/INCONCLUSIVE, software pipeline всё равно полезна |
| S2 Failed Breakout→Reclaim / R1 | новый path-dependent known-level→breach→reclaim, не external LaneB failed-continuation | OHLCV + point-in-time confirmed levels; средняя FSM/expiry | anchor после breach, hindsight pivot, same-bar ordering, задержка reclaim | первый новый structural study после baseline/ablations | эффект отсутствует OOS/после publication, требует hindsight или не отличается от no-prior-breach control → reject |
| S3 Breakout→Retest→Continuation / R2 | новый breakout-then-later-retest, не Center retest текущего канала | OHLCV/levels; средняя-высокая state/provenance | будущий pivot, retest до breakout, missed run-away moves, label selection | после R1 | нет преимущества к direct breakout/SETUP-02 после ожидания и costs; ambiguous path без данных → inconclusive |
| S4 Trend Pullback | механизм SETUP-02 уже есть; полный Core open/close/candle/quality contract точнее B | низкая data/новая formula не нужна; integration как S1 | sticky Pine trend/width gates не совпадают с Core; delayed HTF | baseline, не копия strategy | как S1, сравнивать отдельно, не считать overlap двух families независимым успехом |
| S5 Flow Extreme Recovery | отсутствует; три wave families и timing как отдельные модели | OHLCV достаточно для source transform; средняя реализация, высокая validation surface | built-in seeds/na, normalization window, threshold selection, late recovery, last-bar POC contamination | после stable baseline и price-only R1; Flow-only controls отдельно | нет incremental OOS/published-time value сверх price/volume/R1; эффект только поздним подтверждением прошедшего движения → reject additive filter |
| S6 Compression→Expansion | ATR/SD есть, нормализованное compression/expansion episode правило отсутствует | OHLCV; средняя code, высокая search/multiple-testing cost | future quantiles/regime selection, expansion уже произошла до publication | последней из шести | ordinary breakout даёт то же/лучше; работает лишь на подобранных окнах/TF → reject; неопределённые quantiles/data → UNKNOWN |

## 4. Frozen protocol: три независимых доказательства

### 4.1 Software functionality

Unit/integration/replay/restart проверяют formula boundaries, provenance, durable atomicity и доставку. Public smoke проверяет настоящий adapter и close→decision→record→output path. Ни высокая code coverage, ни synthetic profitable fixture не доказывают predictive value. Настоящие существующие проверки и два causal defects — в review; Pine parity **не выполнена**.

Для будущего SR acceptance: один запуск с no-setup/unknown тоже валиден, если входы, причины и отсутствие side effects наблюдаемы. Не ослаблять rules ради красивой карточки. Program default scaffold и существующий operational shadow не заменяют этот smoke.

### 4.2 Causal predictive value

До оценки заморозить:

- study/dataset/run identities, source hashes и coverage/exclusions; symbol listing/delisting timelines и point-in-time universe/selection snapshots, не сегодняшний surviving список;
- native setup/HTF TF, UTC boundaries, raw close и endExclusive conventions, receive/availability provenance (recorded или явно modeled), stable order, late/correction policy;
- family/formula/config/seed-history versions, все inspected parameter/TF/filter trials, before/same/after timing definition;
- train/development → validation → один untouched OOS; chronological walk-forward обучается только на прошлом; frozen OOS не превращается снова в OOS после просмотра и подстройки;
- target labels, горизонты, scenario invalidation/expiry, primary estimand/null, минимальный practically relevant effect и требуемая точность/CI/multiplicity policy. Если они не объявлены — нет accept gate, а не выдуманный threshold;
- label observation source, ambiguity/censoring/missing-data policy, known costs и latency scenarios; configuration/universe/labels/cost hashes входят в manifest.

Purging/embargo должны иметь точный алгоритм и temporal dependency intervals. Для каждой opportunity хранить information cut и label interval `[labelStart,labelEnd]`; при split удалять training labels, чьи outcome/information dependencies пересекают held-out interval по предобъявленному правилу, и применять объявленный forward embargo. Его длительность выводится из label overlap/доступности/зависимости, а не из желаемого результата. Совместное использование доступной прошлой feature history само по себе не доказывает leakage; shared labels и fitting на будущем — другое. Не применять случайный split соседних баров. Предварительно публиковать split assignments и algorithm version.

Кластеризация opportunities по времени/инструментам и serial dependence учитываются в effective sample и confidence intervals; block/cluster bootstrap либо иной заранее выбранный метод, не IID binomial certainty для перекрывающихся сигналов. Все семьи/TF/параметры учитываются в multiple comparisons; отрицательные исследования не удаляются переименованием family.

### 4.3 Реальная пользовательская полезность

Отдельно считать model outcomes после `decisionAt` и user-observable outcomes после channel-specific `publishedAt` + явно объявленной/измеренной reaction latency. При отсутствии исторических delivery timestamps это **latency scenario**, не реконструированный факт. Signal-bar close — reference observation, не обещанный fill.

Hypothetical cost-aware model — отдельный output, без настоящих/virtual order intents и позиций. Entry/exit conventions, non-fill/gap assumptions, fee обеих сторон, spread, slippage, latency и funding timestamps регистрируются. Без BBO/trade path нет точного net expectancy: bounds/UNKNOWN. Drawdown появляется только у отдельной явно заданной портфельной симуляции, не автоматически у потока alerts; она не требуется baseline signal software.

## 5. Общая opportunity table и метрики

Одна строка opportunity — предобъявленный `(venue, symbol, eligible 15m interval, study version)` либо episode key по заранее заданному generation rule. Сравнивать полный календарь eligible universe, включая no-signal, unknown, blocked, overlapping и unmatched events; не только удачные пересечения разных generators. Хранить exclusion reasons и denominators по каждой series. Оба направления и несколько families связаны cluster/overlap keys, не объявляются независимыми samples.

| Метрика | Предобъявляемое определение / ограничение |
|---|---|
| unique opportunities, episode/signal counts | candidate generation/rearm policy fixed; повтор доставки не увеличивает sample |
| quality/coverage | expected intervals × PIT universe; closed eligible, missing/stale/gap/corrected/conflict counts; fraction Unknown, их причины; window coverage отдельно от dataset coverage |
| frequency/expiry | unique scenarios на covered symbol-time; no-signal denominator сохраняется |
| delayed publication | close→availability→decision→first attempt→API accepted; latency distribution и expiry-before-publication; no implicit zero latency |
| directional hit rate | `sign(direction*(P(t+h)-P_ref))>0` на declared horizon и reference source; ties, censored и unknown явно отдельно, не скрыто выброшены |
| signed forward returns | `direction*(P(t+h)/P_ref-1)`; распределение и conditional incremental differences к control, не только mean/win rate |
| MAE/MFE/time-to-outcome | extrema **после** соответствующего decision/publish boundary до frozen horizon; требует доступного path. Полный high/low бара, содержащего публикацию внутри него, не гарантированно произошёл после публикации |
| target-before-invalidation | first-passage rule; при обоих уровнях в одном OHLC bar — detailed legal path либо pessimistic/optimistic bounds, не выбранный задним числом порядок |
| hypothetical net expectancy | отдельная модель после latency/cost/funding/non-fill, units и assumptions explicit; не фактическая торговая доходность |
| blocked-good / blocked-bad | independent fixed outcome label на общей price-only candidate table; пропущенные хорошие и предотвращённые плохие, вместе с delay и frequency change |
| uncertainty/robustness | preregistered CI/multiplicity, time/symbol clustered dependence, regime/liquidity/direction splits, leave-one-instrument-out, cost/latency stress, parameter-neighborhood sensitivity |

Результаты обязаны содержать dataset/run/config/split IDs и source hashes. В этом аудите **нет** значений hit rate, expectancy, успешности S1–S6 или оптимальных periods.

## 6. Карточки исследований: prerequisites, H0, controls, leakage, отказ

Для всех строк minimum evidence = validated causal dataset/coverage, полностью frozen manifest и известный estimand, воспроизводимый run, независимый OOS/prospective split и CI достаточной объявленной точности. Пока этого нет, family — RESEARCH; source chart — не результат. `H0: Δ<=0` означает отсутствие положительной incremental value по primary predeclared estimand, не произвольный win-rate threshold.

| Study / distinct identity | Prerequisite data и manifest rules | H0 / baseline | Leakage risks | Рассмотреть дальше / reject / inconclusive |
|---|---|---|---|---|
| SETUP-01 15m/1h | native closed bars, confirmed HTF history; explicit price-context/quality config, full absolute width minimum, seed/freshness/expiry/rearm, scenario label | no predictive improvement к price/volume и MA-only на общей table | late/corrected bars, future pivots/HTF, signal close credit до publish | software delivery независимо от edge; statistical promotion только по frozen OOS; effect absent→not validated; coverage failure→UNKNOWN |
| SETUP-02 15m/1h | те же данные; exact open+close hold/candle direction; без введения minimum-width | не лучше price/volume/MA-only и сопоставимого trend opportunity control | mislabeled Pine crossing body, overlapping S1 episodes | сравнить отдельно и overlap-aware; только intersection winners→invalid study |
| MA-only / price-volume control | explicit SMA/EMA kind/period/seed/warmup/volume normalization; no ZIP binary | Δ structured vs control<=0 | выбирать best MA после OOS, корреляцию нескольких MA выдавать независимыми голосами | остаётся control даже без собственного edge; точная формула нужна до comparisons |
| Simple Lane A ablations | known level age от confirmation availability; efficiency `abs(Ct-Ct-n)/sum(abs(ΔC))`, zero denominator Unknown; excursions/acceptance/duration definition | feature не улучшает ту же baseline opportunity set | future age/ATR window, множество filters и selected covered subset | один признак/версия за раз, matched table + blocked-good; delay/cost уничтожает эффект→reject |
| R1 / S2 | level ID/role/anchor зафиксированы до breach; `LevelKnown→Breach→Reclaim→Candidate`, expiry/invalidation; same-bar разрешение явно, отдельный path если нужно | не лучше Boundary и reclaim без prior breach | level из будущего pivot, intrabar sequence, смена role после просмотра движения | ценность остаётся после reclaim publication/OOS; hindsight или исчезновение increment→reject |
| R2 / S3 | `KnownLevel→ClosedBreakout→LaterRetest→Hold`, direction/reset/lifetime/distance/horizon frozen | не лучше direct breakout и SETUP-02 | retest раньше breakout; будущая structural зона; исключение runaway opportunities | учитывать missed moves; requires hindsight→reject; insufficient path→INCONCLUSIVE |
| Flow-Stochastic-only | OHLCV + exact flowSource provenance, mintick; raw range, smoothing, thresholds, min extreme bars, FSM and normalization policy | не лучше equivalent price/volume control | short history fallback, thresholds/data snooping, last-bar POC | независимые TV/.NET formula vectors перед performance; нет incremental value→reject promotion |
| Flow-RSI-only | OHLCV/source, `ta.rsi` gain/loss seed/na/zero contract; separate family/version | не лучше price/volume и простого registered RSI control | seed/history mismatch, same data tuned thresholds | не объединять с stochastic winner в один OOS; no precision→INCONCLUSIVE |
| Flow-ATR-only | OHLCV/source, explicit MA kind/seed + ATR/scale/clamp and warmup | не лучше normalized price-vs-center control | fallback50 как готовое значение, changing normalization/ATR | stable OOS increment к тому же normalized control нужен; нет→reject additive value |
| Flow + R1 timing | price-only R1 table; каждый wave отдельный; before/same/after by **availability**, windows/confirmation latency frozen | каждый timing variant не лучше R1 без Flow | Flow после reclaim кредитует уже пройденный ход; only coincident winners | price-only control обязателен; позднее подтверждение без user value→reject; no event count→INCONCLUSIVE |
| 30m / 1h setup TF | отдельные native series, PIT calendar, закрытый старший TF выбран заранее; same-bar-count и same-duration окна — **два** исследования | не лучше declared 15m control under comparable estimand | выбор TF после OOS, физические окна/частоты несопоставимы | не менять production TF даже при exploratory winner; нужна отдельная owner amendment |
| OB / POC ablations | causal OB birth not painted anchor; exact trades/bin/window либо явно OHLCV approximation, не смешивать | не добавляют value к price-only R1/R2 | last-bar display POC, hindsight source candle, bins/window fitting | comparable covered subsets + full coverage loss; optional feature не покрыт→UNKNOWN, effect отсутствует→reject |
| Lane B / C | recorded OI/funding kind/units/availability/liquidation coverage; для C actual depth U/u/pu/snapshots/trades/gaps | не добавляют OOS value к той же price/volume table | funding forecast vs payment; incomplete liquidation prints; contemporaneous flow vs future returns; capture selection | без пригодного coverage только diagnostics; Δ absent→не mandatory filter |
| S6 / complex ensembles / ML | historical volatility reference/quantile trained only on past, compression/release FSM; ensembles и router имеют свои versions/trial counts | не лучше ordinary breakout / unconditioned simpler families | future regime/quantiles, feature fishing, router misclassification, overlapping labels | только после простых controls; complexity без OOS increment→reject; dataset/power insufficient→INCONCLUSIVE |

## 7. Порядок и граница решений

1. SR software baseline SETUP-01/02 15m/1h и honest outcomes.
2. Frozen MA-only/price-volume controls и по одной Lane A ablation.
3. Price-only R1, затем независимый R2.
4. Три Flow-only исследования; затем Flow+R1 before/same/after и price-only control.
5. OB/POC, optional Lane B/C только при измеримом coverage/increment.
6. S6, regime routing, ensembles/ML — последними.

30m/1h setup comparisons допускаются в зарегистрированной research branch, не как prerequisite SR-01…06 и не автоматическая смена TF. SR-07 вне critical path готовности baseline. Ни один успешный statistical result не снимает P17 или не создаёт `OrderAuthorized`; выбранные текущие решения не требуют private credentials.
