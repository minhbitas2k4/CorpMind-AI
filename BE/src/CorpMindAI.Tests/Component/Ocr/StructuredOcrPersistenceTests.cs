using System.Net;
using System.Text;
using System.Text.Json;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Usecase.Document.Query;
using CorpMindAI.Application.Usecase.Document.Command;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.Component.Ocr;

[Trait("TestType", "Component")]
public class StructuredOcrPersistenceTests
{
    private const string PythonResponse = """
    {
      "document_id":"42","schema_version":"1.0","status":"success","total_pages":1,
      "page_average_confidence":0.97,"overall_level":"high",
      "components":[{"component_type":"figure","raw_text":"Figure text","average_confidence":0.95,"level":"high","bbox":[10,20,300,220]}],
      "validation_errors":[{"component_type":"text","text":"review","confidence":0.4,"reason":"low_confidence"}],
      "pages":[{"page_number":1,"width":1654,"height":2339,"render_dpi":200,
        "coordinate_system":{"origin":"top_left","bbox_format":"xyxy","unit":"pixel"},
        "components":[{"component_id":"42-p1-c0001","document_id":"42","page_number":1,"type":"figure",
          "bbox":[10,20,300,220],"normalized_bbox":[0.006,0.009,0.181,0.094],"reading_order":0,
          "confidence":0.95,"text":"Figure text",
          "lines":[{"line_id":"42-p1-c0001-l0001","text":"Figure text","confidence":0.95,
            "bbox":[[12,24],[280,24],[280,48],[12,48]],"normalized_bbox":[[0.007,0.010],[0.169,0.010],[0.169,0.021],[0.007,0.021]]}],
          "metadata":{"source_layout_type":"figure","synthetic_region":false,"asset_errors":[]},
          "rows":null,"cells":null,
          "asset":{"asset_id":"asset-1","type":"image","path":"assets/figure.png","width":290,"height":200,"source":"page_crop"},
          "caption":{"text":"Architecture diagram","lines":[{"line_id":"caption-1","text":"Architecture diagram","confidence":0.93,
            "bbox":[[10,225],[250,225],[250,245],[10,245]],"normalized_bbox":[[0.006,0.096],[0.151,0.096],[0.151,0.105],[0.006,0.105]]}]}
        }]}],"asset_processing_seconds":0.01,
      "reconstruction_artifact":{"artifact_id":"11111111-1111-1111-1111-111111111111","document_id":"42","file_name":"reconstructed.pdf","content_type":"application/pdf","size":15,"render_seconds":0.1,"warnings":[]}
    }
    """;

    [Fact]
    public void Ocr_background_job_disables_automatic_retries()
    {
        var execute = typeof(OcrProcessingJob).GetMethod(nameof(OcrProcessingJob.Execute));
        var retry = Assert.Single(execute!.GetCustomAttributes(typeof(AutomaticRetryAttribute), false)
            .Cast<AutomaticRetryAttribute>());

        Assert.Equal(0, retry.Attempts);
        Assert.Equal(AttemptsExceededAction.Fail, retry.OnAttemptsExceeded);
    }

    [Fact]
    public async Task Terminal_ocr_failure_is_persisted_and_visible_in_status_response()
    {
        await using var context = CreateContext();
        context.Departments.Add(new Department { Id = 3, Name = "Support" });
        var sourcePath = Path.GetTempFileName();
        try
        {
            context.Documents.Add(new Document
            {
                Id = 31, OriginalFileName = "failed.pdf", Title = "failed",
                StorageKey = "documents/failed.pdf", FileType = "application/pdf",
                DepartmentId = 3, UploadedById = 8, Status = "queued", OcrStatus = "queued"
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var repository = new DocumentRepository(context);
            var handler = new ProcessOcrJobCommandHandler(
                repository, new FakeFileStorage(sourcePath, false), new FailedOcrService(),
                new FakeUnitOfWork(context, repository),
                NullLogger<ProcessOcrJobCommandHandler>.Instance,
                new ChunkingOutbox(context));

            var response = await handler.Handle(new ProcessOcrJobCommand(31, 8), CancellationToken.None);
            context.ChangeTracker.Clear();
            var persisted = await context.OcrResults.SingleAsync(result => result.DocumentId == 31);
            var document = await context.Documents.SingleAsync(result => result.Id == 31);
            var status = await new GetOcrStatusQueryHandler(
                repository, NullLogger<GetOcrStatusQueryHandler>.Instance)
                .Handle(new GetOcrStatusQuery(31, 8), CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("failed", document.OcrStatus);
            Assert.Equal("failed", persisted.Status);
            Assert.Equal("forced native OCR failure", persisted.ErrorMessage);
            Assert.Null(persisted.StructuredDocumentJson);
            Assert.Equal("forced native OCR failure", status.Data!.ErrorMessage);
            Assert.Null(status.Data.Result);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task Python_contract_preserves_complete_structured_document_and_legacy_fields()
    {
        var client = new HttpClient(new StubHandler(PythonResponse)) { BaseAddress = new Uri("http://ocr.test") };
        var service = new OcrHttpClient(client, NullLogger<OcrHttpClient>.Instance);

        var result = await service.ProcessOcrAsync(42, "document.pdf");
        var document = JsonSerializer.Deserialize<StructuredDocumentDto>(result.StructuredDocumentJson!);
        var component = Assert.Single(Assert.Single(document!.Pages).Components);
        var line = Assert.Single(component.Lines);

        Assert.True(result.Success);
        Assert.Equal("1.0", result.SchemaVersion);
        Assert.Equal("11111111-1111-1111-1111-111111111111", result.ReconstructionArtifact!.ArtifactId);
        Assert.Equal("42", document.DocumentId);
        Assert.Equal(1, document.TotalPages);
        Assert.Equal((1, 1654, 2339, 200), (document.Pages[0].PageNumber, document.Pages[0].Width, document.Pages[0].Height, document.Pages[0].RenderDpi));
        Assert.Equal(("top_left", "xyxy", "pixel"), (document.Pages[0].CoordinateSystem.Origin, document.Pages[0].CoordinateSystem.BboxFormat, document.Pages[0].CoordinateSystem.Unit));
        Assert.Equal("42-p1-c0001", component.ComponentId);
        Assert.Equal("figure", component.Type);
        Assert.Equal(new double[] { 10, 20, 300, 220 }, component.Bbox);
        Assert.Equal(new double[] { 0.006, 0.009, 0.181, 0.094 }, component.NormalizedBbox);
        Assert.Equal(0, component.ReadingOrder);
        Assert.Equal("Figure text", component.Text);
        Assert.Equal("42-p1-c0001-l0001", line.LineId);
        Assert.Equal(new double[] { 12, 24 }, line.Bbox[0]);
        Assert.Equal("figure", component.Metadata.SourceLayoutType);
        Assert.Equal("asset-1", component.Asset!.AssetId);
        Assert.Equal("Architecture diagram", component.Caption!.Text);
        Assert.Null(component.Rows);
        Assert.Null(component.Cells);

        var legacy = Assert.Single(OcrContractDeserializer.DeserializeComponents(result.ComponentsJson));
        var validation = Assert.Single(OcrContractDeserializer.DeserializeValidationErrors(result.ValidationErrorsJson));
        Assert.Equal(("figure", "Figure text", 0.95), (legacy.ComponentType, legacy.RawText, legacy.AverageConfidence));
        Assert.Equal(("text", "review", 0.4, "low_confidence"), (validation.ComponentType, validation.Text, validation.Confidence, validation.Reason));

        await using var destination = new MemoryStream();
        var bytes = await service.DownloadReconstructionAsync(42, result.ReconstructionArtifact.ArtifactId, destination);
        Assert.Equal(14, bytes);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(destination.ToArray()));
        await service.CleanupReconstructionAsync(42, result.ReconstructionArtifact.ArtifactId);
    }

    [Fact]
    public async Task Structured_and_legacy_values_are_stored_and_old_null_values_remain_readable()
    {
        await using var context = CreateContext();
        var repository = new DocumentRepository(context);
        var structured = "{\"schema_version\":\"1.0\",\"document_id\":\"7\",\"total_pages\":0,\"pages\":[]}";
        context.Departments.Add(new Department { Id = 1, Name = "Operations" });
        context.Documents.AddRange(
            new Document { Id = 7, OriginalFileName = "new.pdf", Title = "new", StorageKey = "documents/new.pdf", DepartmentId = 1, UploadedById = 77, Status = "completed", OcrStatus = "completed" },
            new Document { Id = 8, OriginalFileName = "old.pdf", Title = "old", StorageKey = "documents/old.pdf", DepartmentId = 1, UploadedById = 77, Status = "completed", OcrStatus = "completed" });

        await repository.AddOcrResultAsync(new OcrResult
        {
            DocumentId = 7, TotalPages = 0, ComponentsJson = "[]", ValidationErrorsJson = "[]",
            SchemaVersion = "1.0", StructuredDocumentJson = structured, Status = "completed"
        });
        await repository.AddOcrResultAsync(new OcrResult
        {
            DocumentId = 8, TotalPages = 1, ComponentsJson = "[]", ValidationErrorsJson = "[]",
            SchemaVersion = null, StructuredDocumentJson = null, Status = "completed"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var current = await repository.GetOcrResultByDocumentIdAsync(7);
        var old = await repository.GetOcrResultByDocumentIdAsync(8);
        Assert.Equal("1.0", current!.SchemaVersion);
        Assert.Equal(structured, current.StructuredDocumentJson);
        Assert.Equal("[]", current.ComponentsJson);
        Assert.Equal("[]", current.ValidationErrorsJson);
        Assert.Null(old!.SchemaVersion);
        Assert.Null(old.StructuredDocumentJson);
        Assert.Equal("[]", old.ComponentsJson);

        var query = new GetOcrStatusQueryHandler(repository, NullLogger<GetOcrStatusQueryHandler>.Instance);
        var oldResponse = await query.Handle(new GetOcrStatusQuery(8, 77), CancellationToken.None);
        Assert.True(oldResponse.Success);
        Assert.NotNull(oldResponse.Data!.Result);
        Assert.Null(oldResponse.Data.Result.StructuredDocument);
        Assert.Empty(oldResponse.Data.Result.Components);
    }

    [Fact]
    public async Task Command_repository_lookup_tracks_document_so_ocr_status_is_persisted()
    {
        await using var context = CreateContext();
        context.Departments.Add(new Department { Id = 1, Name = "Engineering" });
        context.Documents.Add(new Document
        {
            Id = 9, OriginalFileName = "doc.pdf", Title = "doc", StorageKey = "documents/doc.pdf",
            DepartmentId = 1, Status = "uploaded", OcrStatus = "not_started"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new DocumentRepository(context);

        var document = await repository.GetByIdAsync(9);
        document!.OcrStatus = "queued";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal("queued", (await context.Documents.SingleAsync(d => d.Id == 9)).OcrStatus);
    }

    [Fact]
    public async Task Failed_completion_save_does_not_persist_a_chunking_outbox_message()
    {
        await using var context = CreateContext();
        context.Departments.Add(new Department { Id = 22, Name = "Atomic outbox" });
        var sourcePath = Path.GetTempFileName();
        try
        {
            context.Documents.Add(new Document
            {
                Id = 21, OriginalFileName = "atomic.pdf", Title = "atomic",
                StorageKey = "documents/atomic.pdf", FileType = "application/pdf",
                DepartmentId = 22, Status = "queued", OcrStatus = "queued"
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var repository = new DocumentRepository(context);
            var handler = new ProcessOcrJobCommandHandler(
                repository,
                new FakeFileStorage(sourcePath, false),
                new FakeOcrService(false),
                new FailSecondSaveUnitOfWork(context, repository),
                NullLogger<ProcessOcrJobCommandHandler>.Instance,
                new ChunkingOutbox(context));

            var response = await handler.Handle(new ProcessOcrJobCommand(21, 5), CancellationToken.None);

            context.ChangeTracker.Clear();
            Assert.False(response.Success);
            Assert.False(await context.ChunkingOutboxMessages.AnyAsync());
            Assert.Equal("failed", (await context.Documents.SingleAsync()).OcrStatus);
            Assert.Equal("failed", (await context.OcrResults.SingleAsync()).Status);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Theory]
    [InlineData(false, false, "completed")]
    [InlineData(true, false, "failed")]
    [InlineData(false, true, "failed")]
    public async Task Reconstruction_failure_does_not_discard_successful_structured_ocr(
        bool transferFails, bool storageFails, string expectedStatus)
    {
        await using var context = CreateContext();
        context.Departments.Add(new Department { Id = 2, Name = "Legal" });
        var sourcePath = Path.GetTempFileName();
        var transferRoot = Path.Combine(Path.GetTempPath(), "CorpMindAI", "ocr-artifact-transfer");
        Directory.CreateDirectory(transferRoot);
        var abandonedTransfer = Path.Combine(transferRoot, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(abandonedTransfer, "abandoned");
        File.SetLastWriteTimeUtc(abandonedTransfer, DateTime.UtcNow.AddDays(-2));
        try
        {
            context.Documents.Add(new Document
            {
                Id = 21, OriginalFileName = "legal.pdf", Title = "legal",
                StorageKey = "documents/legal.pdf", FileType = "application/pdf",
                DepartmentId = 2, UploadedById = 5, Status = "queued", OcrStatus = "queued"
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var repository = new DocumentRepository(context);
            var storage = new FakeFileStorage(sourcePath, storageFails);
            var ocr = new FakeOcrService(transferFails);
            var unitOfWork = new FakeUnitOfWork(context, repository);
            var handler = new ProcessOcrJobCommandHandler(
                repository, storage, ocr, unitOfWork,
                NullLogger<ProcessOcrJobCommandHandler>.Instance,
                new ChunkingOutbox(context));

            var response = await handler.Handle(new ProcessOcrJobCommand(21, 5), CancellationToken.None);
            context.ChangeTracker.Clear();
            var persisted = await context.OcrResults.SingleAsync(result => result.DocumentId == 21);
            var document = await context.Documents.SingleAsync(result => result.Id == 21);

            Assert.True(response.Success);
            Assert.False(File.Exists(abandonedTransfer));
            Assert.Equal("completed", document.OcrStatus);
            Assert.NotNull(persisted.StructuredDocumentJson);
            Assert.Single(context.ChunkingOutboxMessages);
            Assert.Equal(expectedStatus, persisted.ReconstructionStatus);
            if (expectedStatus == "completed")
            {
                Assert.Equal("documents/21/reconstructed.pdf", persisted.ReconstructedStorageKey);
                Assert.False(Path.IsPathRooted(persisted.ReconstructedStorageKey));
                Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(storage.StoredBytes!));
                Assert.Equal(1, ocr.CleanupCalls);
            }
            else
            {
                Assert.Null(persisted.ReconstructedStorageKey);
                Assert.Equal(0, ocr.CleanupCalls);
            }
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(abandonedTransfer);
        }
    }

    private static CorpMindDbContext CreateContext() => new(
        new DbContextOptionsBuilder<CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("%PDF-test-data"u8.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
                    }
                });
            if (request.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeOcrService(bool transferFails) : IOcrService
    {
        public int CleanupCalls { get; private set; }

        public Task<OcrServiceResponse> ProcessOcrAsync(int documentId, string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrServiceResponse
            {
                Success = true, TotalPages = 1, PageAverageConfidence = 0.99, OverallLevel = "high",
                ComponentsJson = "[]", ValidationErrorsJson = "[]", SchemaVersion = "1.0",
                StructuredDocumentJson = "{\"schema_version\":\"1.0\",\"document_id\":\"21\",\"total_pages\":1,\"pages\":[]}",
                ReconstructionArtifact = new ReconstructionArtifactResponse
                {
                    ArtifactId = "11111111-1111-1111-1111-111111111111", DocumentId = "21", Size = 14
                }
            });

        public async Task<long> DownloadReconstructionAsync(int documentId, string artifactId, Stream destination, CancellationToken cancellationToken = default)
        {
            if (transferFails)
                throw new HttpRequestException("artifact unavailable");
            var bytes = "%PDF-test-data"u8.ToArray();
            await destination.WriteAsync(bytes, cancellationToken);
            return bytes.Length;
        }

        public Task CleanupReconstructionAsync(int documentId, string artifactId, CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailedOcrService : IOcrService
    {
        public Task<OcrServiceResponse> ProcessOcrAsync(int documentId, string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrServiceResponse
            {
                Success = false,
                ErrorMessage = "forced native OCR failure"
            });

        public Task<long> DownloadReconstructionAsync(int documentId, string artifactId, Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CleanupReconstructionAsync(int documentId, string artifactId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFileStorage(string sourcePath, bool storageFails) : IFileStorage
    {
        public byte[]? StoredBytes { get; private set; }
        public string GetPhysicalPath(string storageKey) => sourcePath;
        public Task<string> UploadAsync(Stream fileStream, string originalFileName, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<string> UploadReconstructionAsync(Stream fileStream, int documentId, CancellationToken cancellationToken = default)
        {
            if (storageFails)
                throw new IOException("storage failed");
            using var copy = new MemoryStream();
            await fileStream.CopyToAsync(copy, cancellationToken);
            StoredBytes = copy.ToArray();
            return $"documents/{documentId}/reconstructed.pdf";
        }
    }

    private sealed class FakeUnitOfWork(CorpMindDbContext context, IDocumentRepository documents) : IUnitOfWork
    {
        public IUserRepository UserRepo => null!;
        public IDocumentRepository DocumentRepo => documents;
        public IChunkRepository ChunkRepo => null!;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
        public Task BeginTransactionAsync() => Task.CompletedTask;
        public Task CommitTransactionAsync() => Task.CompletedTask;
        public Task RollbackTransactionAsync() => Task.CompletedTask;
        public void ClearTrackedChanges() => context.ChangeTracker.Clear();
        public void Dispose() { }
    }

    private sealed class FailSecondSaveUnitOfWork(
        CorpMindDbContext context,
        IDocumentRepository documents) : IUnitOfWork
    {
        private int _saveCount;
        public IUserRepository UserRepo => null!;
        public IDocumentRepository DocumentRepo => documents;
        public IChunkRepository ChunkRepo => null!;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCount++;
            return _saveCount == 2
                ? Task.FromException<int>(new DbUpdateException("forced completion failure"))
                : context.SaveChangesAsync(cancellationToken);
        }

        public Task BeginTransactionAsync() => Task.CompletedTask;
        public Task CommitTransactionAsync() => Task.CompletedTask;
        public Task RollbackTransactionAsync() => Task.CompletedTask;
        public void ClearTrackedChanges() => context.ChangeTracker.Clear();
        public void Dispose() { }
    }
}
