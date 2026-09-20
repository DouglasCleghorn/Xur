# Xur app update 2026.09.15.1

- Token login now opens account creation. Save a username/password for future
  logins; the token disappears from the console and is disabled. Existing token
  sessions on installations without an account lead to this same setup form.
- Profile names are editable. Load profile starts switching immediately; Preview
  opens the change list separately, and Edit is under More. Changing a name keeps
  the same workload identities.
- Containers supports Podman image pulls and Dockerfile builds, optional text
  build files, GPU vendor/count, commands, environment, HTTP health checks, and
  named persistent volumes. Select prepared containers from the profile editor.
  Storage shows their volume usage and saved workload associations.
- vLLM search accepts full Hugging Face URLs and exact repository IDs. It no longer
  excludes multimodal or vLLM-tagged checkpoints by requiring text-generation and
  transformers tags. This fixes discovery of
  `lued/Qwen3.8-27B-INT8-W8A16-MTP`. Execution still requires an eligible NVIDIA
  device and a compatible engine; this update does not claim a GPU inference run
  for that checkpoint.

See [Manager account](../architecture/manager-account.md) for the new automation endpoints and
[Container workloads](../usage/container-workloads.md) for supported fields and limits.

The update changes the Xur app bundle. It does not rebuild the installer ISO.
The installer source also carries the new account handoff for the next ISO build.
