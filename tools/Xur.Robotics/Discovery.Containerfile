FROM mirror.gcr.io/library/python:3.14-slim-bookworm

# LeRobot's utility imports require CPU Torch and Hugging Face Hub even for
# motor discovery. No policies or model weights are downloaded.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu 'torch==2.11.0' && \
    pip install --no-cache-dir --no-deps 'lerobot==0.6.0' && \
    pip install --no-cache-dir 'feetech-servo-sdk==1.0.0' 'pyserial==3.5' \
      'deepdiff==8.6.1' 'numpy==2.2.6' 'tqdm==4.67.1' 'draccus==0.10.0' \
      'huggingface-hub==1.33.0' 'packaging==25.0' && \
    python -c "from lerobot.motors.feetech import FeetechMotorsBus"
COPY discover_buses.py /opt/xur/
COPY LICENSE licensing.md /opt/licenses/
ENV PYTHONDONTWRITEBYTECODE=1
ENTRYPOINT ["python", "/opt/xur/discover_buses.py"]
