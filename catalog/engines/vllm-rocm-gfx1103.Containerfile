# Native Radeon 780M stack. Keep the source, AMD wheels and local image tag aligned.
# https://github.com/ROCm/TheRock/blob/main/RELEASES.md
# vLLM 0.31.0 ROCm base; its CPU-only Rust frontend matches the source below.
FROM mirror.gcr.io/vllm/vllm-openai-rocm@sha256:749f6f3f944f12af49966ac523c1f8573e4b229594b541954bd5f879b4496b1f
RUN uv pip uninstall --system torch torchvision torchaudio triton && \
    uv pip install --system --index-url https://stable.repo.amd.com/rocm/whl-next/ --extra-index-url https://pypi.org/simple/ --index-strategy unsafe-best-match \
    "torch[device-gfx1103]==2.13.0+rocm10.0.0" "torchvision[device-gfx1103]==0.28.0+rocm10.0.0" "torchaudio==2.11.0.2+rocm10.0.0" "triton==3.8.0+git4cff872c.rocm10.0.0" "rocm[devel]==10.0.0"
RUN rocm-sdk init && ln -s "$(rocm-sdk path --root)" /opt/rocm-native
ENV ROCM_PATH=/opt/rocm-native ROCM_HOME=/opt/rocm-native HIP_PATH=/opt/rocm-native \
    LD_LIBRARY_PATH=/opt/rocm-native/lib:/opt/rocm-native/lib64 VLLM_ROCM_USE_AITER=0
RUN uv pip uninstall --system amdsmi && uv pip install --system --no-deps --no-build-isolation /opt/rocm-native/share/amd_smi
RUN python3 -c "import torch,torchvision,torchaudio,json; print(json.dumps(dict(torch=torch.__version__,hip=torch.version.hip,archs=torch._C._cuda_getArchFlags()))); assert 'gfx1103' in torch._C._cuda_getArchFlags()"
RUN apt-get update && apt-get install -y --no-install-recommends git g++ && rm -rf /var/lib/apt/lists/*
RUN uv pip install --system "cmake>=3.26.1,<4" ninja "setuptools>=77,<80" "setuptools-scm>=8" "setuptools-rust>=1.9" wheel
WORKDIR /opt/xur-vllm
RUN git init && git remote add origin https://github.com/vllm-project/vllm.git && \
    git fetch --depth=1 origin db9527a46873454610df6dbedf79a36d6bf1a7f6 && git checkout --detach FETCH_HEAD
# Reuse the matching image's CPU-only Rust frontend, compiling all HIP extensions below.
RUN python3 -c "from importlib.metadata import distribution; from pathlib import Path; import shutil; p=distribution('vllm').locate_file('vllm'); [shutil.copy2(f,Path('vllm')/f.name) for f in [p/'vllm-rs', *p.glob('_rust_*.so')] if f.is_file()]"
ENV VLLM_TARGET_DEVICE=rocm PYTORCH_ROCM_ARCH=gfx1103 CMAKE_HIP_ARCHITECTURES=gfx1103 \
    MAX_JOBS=2 CMAKE_BUILD_TYPE=Release SETUPTOOLS_SCM_PRETEND_VERSION=0.31.0
RUN export PATH="/opt/rocm-native/bin:$PATH" && \
    python3 setup.py bdist_wheel --dist-dir=/tmp/xur-wheels && \
    uv pip install --system --no-deps --reinstall /tmp/xur-wheels/*.whl
WORKDIR /workspace
# TheRock PyTorch initializes its packaged ROCm libraries during import. Do this
# before vLLM promotes libtorch_cpu symbols, which otherwise resolves old base libs.
RUN python3 -c "from importlib.metadata import distribution; p=distribution('vllm').locate_file('vllm/env_override.py'); t=p.read_text(); old='\n_maybe_promote_torch_symbols_for_rocm()\n'; assert t.count(old)==1; p.write_text(t.replace(old,'\nimport torch\n_maybe_promote_torch_symbols_for_rocm()\n'))"
RUN python3 -X faulthandler -c "import vllm.entrypoints.cli.main"
RUN python3 -c "import torch,vllm,vllm._C,vllm._C_stable_libtorch,vllm._moe_C_stable_libtorch,vllm._rocm_C; assert 'gfx1103' in torch._C._cuda_getArchFlags(); assert vllm.__version__.startswith('0.31.0')"
ENV VLLM_WORKER_MULTIPROC_METHOD=spawn
LABEL io.xur.amd-gfx-target=gfx1103 io.xur.engine-image="localhost/xur/vllm-rocm-gfx1103:v0.31.0-rocm10.0.0"
ENTRYPOINT ["vllm"]
