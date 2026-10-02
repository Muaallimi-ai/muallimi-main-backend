"""Local embedding sidecar — constitutional local-parity for Phase 1 Stage 7.

Serves multilingual-e5-small (384 dim) behind an OpenAI-compatible
/embeddings endpoint so the backend's LocalEmbeddingProvider needs no
vendor-specific client. Arabic is in-domain for e5-multilingual, which is
why it was picked over all-MiniLM.

e5 models require an input prefix ("passage: " for indexed documents,
"query: " for searches). The prefix lives here rather than in the caller
so the e5-specific contract stays next to the model.
"""

import os

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field
from sentence_transformers import SentenceTransformer

MODEL_NAME = os.getenv("EMBEDDING_MODEL", "intfloat/multilingual-e5-small")
DIM = int(os.getenv("EMBEDDING_DIM", "384"))

app = FastAPI(title="Muaallimi Local Embedding Sidecar")

# Loaded at import, not on first request: the weights are baked into the
# image, so this is a disk read, and it means the container refuses
# connections until it can actually serve.
MODEL = SentenceTransformer(MODEL_NAME)


class EmbeddingRequest(BaseModel):
    input: list[str] | str
    model: str | None = None
    input_type: str = Field(default="document")


@app.get("/healthz")
def healthz():
    return {"status": "ok", "model": MODEL_NAME, "dim": DIM}


@app.get("/healthz/ready")
def ready():
    got = MODEL.get_sentence_embedding_dimension()
    if got != DIM:
        raise HTTPException(500, f"model dim {got} != configured EMBEDDING_DIM {DIM}")
    return {"status": "ready", "model": MODEL_NAME, "dim": got}


@app.post("/embeddings")
def embeddings(req: EmbeddingRequest):
    texts = [req.input] if isinstance(req.input, str) else req.input
    if not texts:
        raise HTTPException(400, "input must be a non-empty string or list of strings")

    prefix = "query: " if req.input_type == "query" else "passage: "
    vectors = MODEL.encode(
        [prefix + t for t in texts],
        normalize_embeddings=True,
    )

    return {
        "object": "list",
        "model": MODEL_NAME,
        "data": [
            {"object": "embedding", "index": i, "embedding": v.tolist()}
            for i, v in enumerate(vectors)
        ],
    }
