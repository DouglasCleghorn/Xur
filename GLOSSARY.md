# Xur

Xur manages a host's workload profiles and supporting services.

## Language

**Profile**:
A named host configuration that selects workloads and, in the Persisted Compute
design, assigns resources to independently running services.
_Avoid_: Service collection, workstation

**Resource assignment**:
The GPUs and resource limits selected for a workload, distinct from whether the
workload is intended to run.
_Avoid_: On/Off state, container identity

**Service defaults**:
A persisted service's saved resource settings, used when the active profile has
no override for that service or no profile is active.
_Avoid_: Last active profile's assignment

**Profile resource override**:
A profile's GPU selection or resource limits for a persisted service, applied
while that profile is active without changing the service's On/Off intent.
_Avoid_: Copy of the container, permission to start an Off service

**Workload**:
An individually named application or workstation managed by Xur.
_Avoid_: Profile, recipe, lifecycle setting

**Recipe**:
A reusable definition of the application a workload runs.
_Avoid_: Running instance, profile

**Persisted Compute**:
The planned Xur section for services that run independently of profiles, with
on/off control and optional shared access to selected GPUs.
_Avoid_: Profile, persistent storage

**Xur system service**:
An application managed by Xur and offered in its service collection, such as
Robotics.
_Avoid_: Arbitrary host service

**Manager origin**:
The browser origin (scheme, hostname, and port) serving Xur's privileged manager
UI.
_Avoid_: URL path as an isolation boundary

**Container frontend**:
The browser UI served by an arbitrary or user-selected container.
_Avoid_: Trusted manager UI merely because Xur starts the container

**Easy HTTPS URL**:
An Xur-managed Tailscale HTTPS address for a container frontend.
_Avoid_: Container port publishing, all application access
