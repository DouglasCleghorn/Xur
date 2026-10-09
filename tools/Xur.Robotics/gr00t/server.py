#!/usr/bin/env python3
"""Private upstream GR00T server/cache prep. No robot devices or motor APIs."""
import argparse
import json
import os
from pathlib import Path

MODEL = "nvidia/GR00T-N1.7-3B"
MODEL_REVISION = "2fc962b973bccdd5d8ce4f67cc63b264d6886495"
COSMOS = "nvidia/Cosmos-Reason2-2B"
COSMOS_REVISION = "9ce19a195e423419c349abfc86fd07178b230561"
DROID = "OXE_DROID_RELATIVE_EEF_RELATIVE_JOINT"


def token(path):
    value = Path(path).read_text().strip()
    if len(value) < 32 or len(value) > 512 or any(c.isspace() for c in value):
        raise ValueError("Use a private token file containing at least 32 non-whitespace characters")
    return value


def configuration(environ):
    mode = environ.get("GR00T_MODE", "benchmark")
    checkpoint = environ.get("GR00T_CHECKPOINT", "/cache/models/n1.7")
    if not checkpoint.startswith("/cache/models/") or ".." in Path(checkpoint).parts:
        raise ValueError("Use a local checkpoint beneath /cache/models")
    checkpoint = str(Path(checkpoint))
    if mode == "benchmark":
        embodiment = DROID
    elif mode == "custom":
        if checkpoint == "/cache/models/n1.7":
            raise ValueError("Custom embodiment requires a fine-tuned checkpoint, not base weights")
        embodiment = "NEW_EMBODIMENT"
    else:
        raise ValueError("GR00T_MODE must be benchmark or custom")
    return {"model_path": checkpoint, "embodiment_tag": embodiment,
            "device": "cuda:0", "strict": True}


def prepare_cache(cache, access_path, upstream_revision):
    # Credentials are mounted only in this explicit preparation container.
    # Hugging Face's download APIs handle gated-access errors; no license
    # acceptance or account mutation is attempted by this tool.
    os.environ.pop("HF_HUB_OFFLINE", None)
    os.environ.pop("HF_DATASETS_OFFLINE", None)
    from huggingface_hub import snapshot_download
    cache.mkdir(parents=True, exist_ok=True)
    (cache / "prepared.json").unlink(missing_ok=True)
    access = token(access_path)
    snapshot_download(MODEL, revision=MODEL_REVISION, token=access,
                      local_dir=cache / "models/n1.7")
    # Cache the pinned backbone under its standard Hugging Face layout.
    # The upstream loader addresses this repo as "main"; bind that local
    # offline ref to the reviewed immutable revision, without fetching main.
    snapshot = snapshot_download(COSMOS, revision=COSMOS_REVISION, token=access,
                                 cache_dir=cache / "huggingface/hub")
    if Path(snapshot).name != COSMOS_REVISION:
        raise RuntimeError("Backbone download returned an unexpected revision")
    ref = Path(snapshot).parent.parent / "refs/main"
    ref.parent.mkdir(parents=True, exist_ok=True)
    ref.write_text(COSMOS_REVISION)
    (cache / "prepared.json.tmp").write_text(json.dumps({
        "model": MODEL, "revision": MODEL_REVISION,
        "upstreamRevision": upstream_revision.read_text().strip(),
        "backbone": COSMOS, "backboneRevision": COSMOS_REVISION,
        "mode": "benchmark", "gpuTested": False, "robotCompatible": False,
    }, indent=2) + "\n")
    (cache / "prepared.json.tmp").replace(cache / "prepared.json")
    print("Model cache prepared. GPU inference and XLeRobot adaptation remain untested.")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("download", "serve", "ping"))
    args = parser.parse_args()
    if args.command == "download":
        prepare_cache(Path("/cache"), Path("/run/secrets/hf-token"), Path("/opt/gr00t-revision"))
        return
    from gr00t.policy.server_client import PolicyClient, PolicyServer
    api_token = token("/run/secrets/policy-token")
    if args.command == "ping":
        with PolicyClient(host="127.0.0.1", port=5555, timeout_ms=5000,
                          api_token=api_token) as client:
            response = client.call_endpoint("ping", requires_input=False)
            if response.get("status") != "ok":
                raise RuntimeError("Policy server is unavailable or rejected authentication")
            print(json.dumps({"status": "ok", "motorCommandsIssued": False}))
        return
    from gr00t.policy.gr00t_policy import Gr00tPolicy
    from gr00t.data.embodiment_tags import EmbodimentTag
    values = configuration(os.environ)
    values["embodiment_tag"] = EmbodimentTag.resolve(values["embodiment_tag"])
    policy = Gr00tPolicy(**values)
    with PolicyServer(policy=policy, host="0.0.0.0", port=5555, api_token=api_token) as server:
        # Remote clients can query predictions, not terminate the GPU service.
        server._endpoints.pop("kill", None)
        server.run()


if __name__ == "__main__":
    main()
