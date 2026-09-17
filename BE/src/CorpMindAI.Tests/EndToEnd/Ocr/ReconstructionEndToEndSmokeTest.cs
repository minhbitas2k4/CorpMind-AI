using System.Text;
using System.Diagnostics;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Usecase.Document.Command;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.EndToEnd.Ocr;

[Trait("TestType", "EndToEnd")]
public class ReconstructionEndToEndSmokeTest
{
    [SkippableFact]
    [Trait("Category", "RealOcrSmoke")]
    public async Task Real_python_pipeline_transfers_reconstruction_into_dotnet_owned_storage()
    {
        var sourcePdf = Environment.GetEnvironmentVariable("CORPMIND_OCR_SMOKE_PDF");
        Skip.If(
            string.IsNullOrWhiteSpace(sourcePdf),
            "Set CORPMIND_OCR_SMOKE_PDF to run the real OCR reconstruction smoke test.");

        var outputRoot = Environment.GetEnvironmentVariable("CORPMIND_OCR_SMOKE_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "corpmind-ocr-smoke");
        var serviceUrl = Environment.GetEnvironmentVariable("CORPMIND_OCR_URL") ?? "http://127.0.0.1:8000";
        var options = new DbContextOptionsBuilder<CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new CorpMindDbContext(options);
        context.Departments.Add(new Department { Id = 91, Name = "Smoke" });
        context.Documents.Add(new Document
        {
            Id = 990001, OriginalFileName = Path.GetFileName(sourcePdf), Title = "smoke",
            StorageKey = "documents/smoke.pdf", FileType = "application/pdf", DepartmentId = 91,
            UploadedById = 9, Status = "queued", OcrStatus = "queued"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var repository = new DocumentRepository(context);
        var storage = new SmokeStorage(sourcePdf, outputRoot);
        var http = new HttpClient { BaseAddress = new Uri(serviceUrl), Timeout = TimeSpan.FromMinutes(20) };
        var ocr = new TimingOcrService(new OcrHttpClient(http, NullLogger<OcrHttpClient>.Instance));
        var unitOfWork = new SmokeUnitOfWork(context, repository);
        var handler = new ProcessOcrJobCommandHandler(
            repository, storage, ocr, unitOfWork, NullLogger<ProcessOcrJobCommandHandler>.Instance,
            new ChunkingOutbox(context));

        var started = DateTime.UtcNow;
        var response = await handler.Handle(new ProcessOcrJobCommand(990001, 9), CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;
        context.ChangeTracker.Clear();
        var result = await context.OcrResults.SingleAsync(row => row.DocumentId == 990001);
        var storedPath = storage.GetPhysicalPath(result.ReconstructedStorageKey!);

        Assert.True(response.Success);
        Assert.NotNull(result.StructuredDocumentJson);
        Assert.Equal("completed", result.ReconstructionStatus);
        Assert.Equal("documents/990001/reconstructed.pdf", result.ReconstructedStorageKey);
        Assert.False(Path.IsPathRooted(result.ReconstructedStorageKey));
        Assert.True(File.Exists(storedPath));
        var signature = new byte[5];
        await using (var stream = File.OpenRead(storedPath))
            Assert.Equal(5, await stream.ReadAsync(signature));
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(signature));
        Console.WriteLine($"REAL_OCR_AND_ARTIFACT_SECONDS={elapsed.TotalSeconds:F3}");
        Console.WriteLine($"ARTIFACT_TRANSFER_STORAGE_SECONDS={(ocr.DownloadElapsed + storage.UploadElapsed).TotalSeconds:F3}");
        Console.WriteLine($"RECONSTRUCTED_PDF={storedPath}");
    }

    private sealed class SmokeStorage(string sourcePdf, string outputRoot) : IFileStorage
    {
        public TimeSpan UploadElapsed { get; private set; }
        public string GetPhysicalPath(string storageKey) => storageKey == "documents/smoke.pdf"
            ? sourcePdf
            : Path.Combine(outputRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        public Task<string> UploadAsync(Stream fileStream, string originalFileName, string contentType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<string> UploadReconstructionAsync(Stream fileStream, int documentId, CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();
            var key = $"documents/{documentId}/reconstructed.pdf";
            var path = GetPhysicalPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = File.Create(path);
            await fileStream.CopyToAsync(output, cancellationToken);
            UploadElapsed = timer.Elapsed;
            return key;
        }
    }

    private sealed class TimingOcrService(IOcrService inner) : IOcrService
    {
        public TimeSpan DownloadElapsed { get; private set; }
        public Task<OcrServiceResponse> ProcessOcrAsync(int documentId, string filePath, CancellationToken cancellationToken = default) =>
            inner.ProcessOcrAsync(documentId, filePath, cancellationToken);
        public async Task<long> DownloadReconstructionAsync(int documentId, string artifactId, Stream destination, CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();
            try { return await inner.DownloadReconstructionAsync(documentId, artifactId, destination, cancellationToken); }
            finally { DownloadElapsed = timer.Elapsed; }
        }
        public Task CleanupReconstructionAsync(int documentId, string artifactId, CancellationToken cancellationToken = default) =>
            inner.CleanupReconstructionAsync(documentId, artifactId, cancellationToken);
    }

    private sealed class SmokeUnitOfWork(CorpMindDbContext context, IDocumentRepository documents) : IUnitOfWork
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
}
