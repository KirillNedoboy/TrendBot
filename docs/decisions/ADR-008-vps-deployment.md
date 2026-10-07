# ADR-008 — VPS deployment

## Context

V1 targets a continuously running Linux VPS with simple operational control and no container/orchestrator requirement.

## Decision

Deploy the application as a systemd service with structured JSON logs routed to journald, environment/secret-store configuration, health checks, and a documented restart policy.

## Alternatives considered

Docker/Kubernetes, a managed serverless runtime, or a Windows service.

## Consequences

Operations must cover systemd units, permissions, disk pressure, journald retention, backups, and restart recovery. CI remains cross-platform even though production is Linux.
