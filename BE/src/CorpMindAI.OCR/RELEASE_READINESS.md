# OCR + Reconstruction V1 lifecycle

- The original uploaded PDF is owned by the .NET `IFileStorage` implementation and
  remains durable under its storage key until the document itself is deleted.
- Table, figure, and unknown component crops are owned by the Python OCR service.
  Their configured `OCR_ASSET_ROOT` paths are persisted in `StructuredDocumentJson`,
  so they must not be treated as temporary files or deleted after OCR.
- The renderer output under `OCR_RECONSTRUCTION_OUTPUT_ROOT` is a transfer artifact.
  Python retains it until .NET has validated and durably stored the PDF, then the
  authenticated internal cleanup call removes that Python copy.
- Rendered 200-DPI page images live in a per-request temporary directory and are
  deleted when processing succeeds or raises a handled exception.
- .NET artifact-transfer files live only under the CorpMindAI-owned
  `ocr-artifact-transfer` temporary directory. Each transfer removes its own file;
  files older than one day are conservatively removed during a later transfer.

Existing Hangfire jobs remain in Hangfire storage across deployments. Startup does
not enqueue OCR work. An old queued OCR invocation runs at most when Hangfire selects
it, and `OcrProcessingJob.Execute` has `AutomaticRetry(Attempts = 0)`. Use an isolated
Hangfire database for release validation when old jobs must not participate.

Paddle retains native inference workspace across documents. Current document-level
serialization and bounded OpenMP/MKL threads are validated for the V1 workload.
Worker process isolation or recycling remains a future operational mitigation only
if long-lived production measurements demonstrate exhaustion.
