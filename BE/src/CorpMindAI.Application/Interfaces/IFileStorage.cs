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
    }
}
