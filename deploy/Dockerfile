FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_DISABLE_PIP_VERSION_CHECK=1 GOOGLE_TOOLS_ROOT=/opt/google-tools
RUN apt-get update && apt-get install -y --no-install-recommends git build-essential && rm -rf /var/lib/apt/lists/*
RUN git clone --filter=blob:none https://github.com/leonboe1/GoogleFindMyTools.git /opt/google-tools && cd /opt/google-tools && git checkout 660be5395702ff7f1a6174f4ec4c865a57c7b527
COPY services/tracking-service/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt -r /opt/google-tools/requirements.txt && rm /tmp/requirements.txt
WORKDIR /app
COPY services/tracking-service/ /app/
USER 1000:1000
EXPOSE 8080
CMD ["python", "-m", "uvicorn", "asgi:app", "--host", "0.0.0.0", "--port", "8080", "--proxy-headers", "--forwarded-allow-ips", "*", "--no-access-log"]
