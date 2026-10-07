# Setup lifecycle

The planned lifecycle is `OBSERVING → SETUP_FORMING → SETUP_CONFIRMED → CONFIRMATION_WINDOW → ENTRY_ARMED`, followed by terminal or cooldown states defined in later phases. Setup identity and idempotency are required so the same event cannot create duplicate entries.

Lifecycle implementation belongs to Phase 8. Phase 9 replay must provide a simulated risk/TP/SL model so lifecycle outcomes can be measured before the independent Risk Engine in Phase 11.
