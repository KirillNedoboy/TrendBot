# TradingBot agent contract

TradingBot is a greenfield .NET 10 modular monolith for deterministic Binance USD-M perpetual research and execution. The current bootstrap contains no trading behavior.

## Startup protocol

Before substantial work, read `AGENTS.md`, `docs/PROJECT_SPEC.md`, `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and `HANDOFF.md`; inspect `git status`; compare those documents with the repository; select the first `READY` task whose dependencies are `DONE`; then make changes. Never rely on a previous chat.

## Routing and scope

The routine executor is Luna/Max (`gpt-5.6-luna`). Sol writes only the short plan for non-trivial work; do not silently substitute another model. Work only on the selected task and preserve unrelated edits. Do not implement future phases early. The bootstrap stops before Core domain contracts.

## Boundaries

`TradingBot.Core` is BCL-only. `TradingBot.Exchange.Binance`, Infrastructure, and Backtesting may reference Core only. App composes the modules. DataTool may reference Infrastructure and Backtesting. Keep tests aligned with these boundaries. Central package versions live in `Directory.Packages.props`; use lock files and do not add ad-hoc packages.

## Security and Git

Never reveal or commit secrets, credentials, private keys, tokens, `.env` files, or production data. Use environment variables or a secret store. Do not deploy, push, force-push, rewrite history, or delete user data without an explicit request. Keep `graft/` ignored and untouched.

## Validation and Done

Run only checks relevant to the changed components. Bootstrap validation is `dotnet restore`, `dotnet restore --locked-mode`, `dotnet format --verify-no-changes`, `dotnet build -c Release --warnaserror`, `dotnet test -c Release`, and `tools/validate-docs.ps1`. A task is `DONE` only after its acceptance criteria and checks pass; update `docs/TASKS.md`, `HANDOFF.md`, and architecture docs when decisions change. GitHub CI is configured but remains unverified until a remote run exists.

## Planning correction invariants

- The canonical `docs/sources/FINAL_TECH_SPEC.md` is immutable and is checked by SHA-256. Amendments belong in `docs/PROJECT_SPEC.md`, `docs/EXECUTION_PLAN.md`, `docs/TASKS.md`, and ADRs.
- Replay preserves event time, exchange time when available, receive/availability time, and stable processing sequence. Shared risk math is separate from fill simulation; private execution, local paper, exchange test, and real-money activation are separate gates.
- Durable order intents, atomic portfolio reservations, reconciliation, partial-fill protection, and unknown/manual-position handling are required before execution work. Lane C bounded research capture starts before private execution and has data-quality observability.
- Out-of-sample inputs and promotion criteria are frozen and predeclared. Unknown parameters remain unknown and no numeric trading threshold is invented in planning documents.
- `tools/validate-docs.ps1` validates dynamic phase/milestone/task graph rules, requirement coverage, status/blocker consistency, cycles, source hash, and the required project graph. `tools/tests/validate-docs.Tests.ps1` is the built-in fixture suite. Startup selection is the first `READY` task in document order whose dependencies are `DONE`; multiple READY tasks are allowed, and zero READY requires an explicit unresolved blocker.
