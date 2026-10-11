# Separate persisted service intent from profile resources

Accepted on 2026-10-11. Persisted services keep one identity, their stored data
and independent On/Off intent. Each service has default resources; an active
profile may override GPU selection and limits. Missing overrides and profile
unload restore defaults. A profile change previews and then performs any required
service restarts, preserving its address and data; Robotics always restarts
disarmed. Off services stay Off. This lets profiles change resource assignments
without becoming the lifetime owner of long-running services, at the cost of
coordinating profile transitions with service reconciliation and explicitly
showing downtime when resources change. Normal profile changes with unchanged
effective resources keep the running instance. Runtime implementation and
hardware validation are still outstanding.
