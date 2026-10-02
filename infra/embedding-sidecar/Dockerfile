FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    HF_HOME=/models \
    EMBEDDING_MODEL=intfloat/multilingual-e5-small \
    EMBEDDING_DIM=384

WORKDIR /app

RUN pip install --no-cache-dir torch==2.5.1 --index-url https://download.pytorch.org/whl/cpu

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Bake the weights into the image so a cold container needs no network —
# that is the whole point of the local-parity provider.
RUN python -c "from sentence_transformers import SentenceTransformer; \
    SentenceTransformer('intfloat/multilingual-e5-small')"

COPY app.py .

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
