FROM python:3.11-slim
WORKDIR /app
COPY src/agents/python/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir --upgrade "pip>=26.0" \
    && pip install --no-cache-dir -r requirements.txt
COPY src/agents/python/app ./app
ENV PORT=8000
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
