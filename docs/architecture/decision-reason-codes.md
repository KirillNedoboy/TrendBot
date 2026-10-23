# Decision reason codes

`DecisionReasonCodes` is the version-one Decision Engine wire catalog. `decision-reasons-v1` fixes each code's literal value, catalog order, and auditable meaning. The catalog exposes immutable `DecisionReasonCodeDefinition` values for JSON evidence, stable constants for production emission, and lane-specific helpers for future/stale/veto/unknown/missing/neutral-required reasons.

Decision Engine evaluation keeps the existing reason sequence and repeated entries. Every emitted reason is validated against the catalog, so an unknown code cannot enter a projection. Projection construction and SQLite read-back also require the recorded catalog version to be supported; unknown versions fail closed without substitution or renaming.
