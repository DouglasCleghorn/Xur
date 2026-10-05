# Xur's XPU Omni layer follows upstream docker/Dockerfile.xpu at v0.30.0:
# https://github.com/vllm-project/vllm-omni/blob/v0.30.0/docker/Dockerfile.xpu
# Keep the vLLM base, Omni source revision and local image tag aligned.
FROM mirror.gcr.io/vllm/vllm-openai-xpu:v0.30.0 AS omni-xpu
RUN apt-get update && apt-get install -y --no-install-recommends espeak-ng ffmpeg git jq && \
    apt-get clean && rm -rf /var/lib/apt/lists/*
WORKDIR /workspace/vllm-omni
RUN git init && git remote add origin https://github.com/vllm-project/vllm-omni.git && \
    git fetch --depth=1 origin a8576ccb725c4e21cd13c3eb5f9a546b21149d2b && git checkout --detach FETCH_HEAD
ENV VLLM_OMNI_TARGET_DEVICE=xpu VLLM_OMNI_VERSION_OVERRIDE=0.30.0+xpu
# Preserve the base's torch/oneCCL/vLLM pairing while resolving Omni dependencies.
RUN python -c "from importlib.metadata import distributions; from pathlib import Path; names={'torch', 'torchaudio', 'torchvision', 'vllm', 'oneccl', 'oneccl-devel'}; Path('/tmp/xpu-constraints.txt').write_text('\n'.join(d.metadata['Name']+'=='+d.version for d in distributions() if d.metadata['Name'].lower() in names))" && \
    uv pip install --no-build-isolation --constraint /tmp/xpu-constraints.txt . && \
    uv pip uninstall triton && uv pip install triton-xpu==3.7.2 --reinstall && \
    python -c "import torch, vllm, vllm_omni; assert torch.xpu._is_compiled(); assert vllm.__version__.split('.')[:2] == ['0', '30']; assert vllm_omni.__version__.split('.')[:2] == ['0', '30']" && \
    rm /tmp/xpu-constraints.txt
ENV VLLM_WORKER_MULTIPROC_METHOD=spawn
ENTRYPOINT ["vllm", "serve", "--omni"]
