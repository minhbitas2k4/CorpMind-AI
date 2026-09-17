using System.Linq.Expressions;

namespace CorpMindAI.Application.Interfaces
{
    public interface IJobScheduler
    {
        string EnqueueFireAndForget<TJob>(Expression<Func<TJob, Task>> methodCall) where TJob : class;
    }
}
