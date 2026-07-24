using System.Linq.Expressions;

namespace CorpMindAI.Application.Interfaces
{
    /// <summary>
    /// Interface abstraction cho job scheduling.
    /// Application layer KHÔNG phụ thuộc trực tiếp vào Hangfire hay bất kỳ library cụ thể nào.
    /// Infrastructure layer sẽ implement interface này bằng Hangfire.
    ///
    /// Tại sao cần abstraction này?
    /// - Clean Architecture: Application layer chỉ biết đến interface, không biết implementation
    /// - Dễ thay thế: muốn chuyển sang Quartz.NET, Azure Functions → chỉ cần đổi implementation
    /// - Dễ test: mock IJobScheduler trong unit test
    /// </summary>
    public interface IJobScheduler
    {
        /// <summary>
        /// Enqueue một fire-and-forget job bất kỳ.
        /// Hangfire sẽ serialize expression và chạy trong background worker.
        /// Worker sẽ resolve TJob từ DI container (Scoped lifetime).
        /// </summary>
        /// <typeparam name="TJob">Kiểu job class (implement trong Infrastructure layer).</typeparam>
        /// <param name="methodCall">Expression gọi method trên TJob với parameters.</param>
        /// <returns>Hangfire Job ID — dùng để theo dõi trạng thái job.</returns>
        string EnqueueFireAndForget<TJob>(Expression<Func<TJob, Task>> methodCall) where TJob : class;
    }
}
