FROM python:3.12-slim

WORKDIR /app

COPY src/agents/deployer/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

COPY src/agents/deployer/deploy.py ./deploy.py

USER 1000
CMD ["python", "/app/deploy.py"]
