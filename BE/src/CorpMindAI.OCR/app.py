from fastapi import FastAPI

app = FastAPI(title="CorpMindAI OCR Service")

@app.get("/")
def root():
    return {
        "service": "CorpMindAI OCR",
        "status": "running"
    }