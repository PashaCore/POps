# POps backend (FastAPI + uvicorn). Built by docker-compose.yml from the repository root;
# see docs/docker.md. Only Backend/ (without tests), the release public key and VERSION
# are copied in (.dockerignore allowlist).
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_ROOT_USER_ACTION=ignore

# The backend is reachable only from the dashboard container on the internal compose
# network, and that proxy always sends a single X-Forwarded-For value (see
# apache-pops.conf). Trusting it lets rate limits and logs see the real client address.
# Do not publish port 8000 to the host while this is set.
ENV FORWARDED_ALLOW_IPS="*"

RUN groupadd --gid 10001 pops \
    && useradd --uid 10001 --gid pops --home-dir /app --no-create-home \
       --shell /usr/sbin/nologin pops

WORKDIR /app

COPY Backend/requirements.txt ./requirements.txt
RUN pip install -r requirements.txt

# Code stays root-owned (read-only for the service user). keys/ and VERSION sit next to
# server.py, where system_routes.py looks for them.
COPY Backend/ ./
COPY keys/ ./keys/
COPY VERSION ./VERSION

# Runtime data (uploads, agent update packages, verified releases): mounted as volumes.
RUN python -m compileall -q /app \
    && mkdir -p storage updates releases \
    && chown pops:pops storage updates releases

USER pops
EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD ["python", "-c", "import json, sys, urllib.request; r = urllib.request.urlopen('http://127.0.0.1:8000/api/health', timeout=4); sys.exit(0 if json.load(r).get('database') else 1)"]

# A single process on purpose: connected agents and panels are tracked in memory.
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000"]
