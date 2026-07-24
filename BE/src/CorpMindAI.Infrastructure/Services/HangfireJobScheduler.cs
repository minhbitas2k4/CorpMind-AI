using System.Linq.Expressions;
using CorpMindAI.Application.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Services
{
    public class HangfireJobScheduler : IJobScheduler
    {
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<HangfireJobScheduler> _logger;

        public HangfireJobScheduler(
            IBackgroundJobClient backgroundJobClient,
            ILogger<HangfireJobScheduler> logger)
        {
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        public string EnqueueFireAndForget<TJob>(Expression<Func<TJob, Task>> methodCall) where TJob : class
        {
            var jobId = _backgroundJobClient.Enqueue(methodCall);

            _logger.LogInformation(
                "Enqueued fire-and-forget job '{JobType}' with JobId={JobId}",
                typeof(TJob).Name,
                jobId);

            return jobId;
        }
    }
}
