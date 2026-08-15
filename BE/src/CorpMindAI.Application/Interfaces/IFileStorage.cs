namespace CorpMindAI.Application.Interfaces
{
    public interface IFileStorage
    {
        Task<string> UploadAsync(
            Stream fileStream,
            string originalFileName,
            string contentType,
            CancellationToken cancellationToken = default);

        Task<Stream> DownloadAsync(
            string storageKey,
            CancellationToken cancellationToken = default);

        Task DeleteAsync(
            string storageKey,
            CancellationToken cancellationToken = default);

        Task<string> UploadReconstructionAsync(
            Stream fileStream,
            int documentId,
            CancellationToken cancellationToken = default);

        string GetPhysicalPath(string storageKey);
    }
}
