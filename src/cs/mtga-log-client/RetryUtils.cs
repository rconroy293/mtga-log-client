using System;
using System.Threading;

namespace mtga_log_client
{
    class RetryUtils
    {

        public static T RetryUntilSuccessful<T>(
            Func<T> callback,
            Func<T, bool> responseValidator,
            Func<Exception, bool> errorValidator,
            TimeSpan initialRetryDelay,
            TimeSpan? maxRetryDelay,
            TimeSpan? maxTotalRetryDuration
        )
        {
            DateTime? lastCallAt = null;
            if (maxTotalRetryDuration.HasValue)
            {
                lastCallAt = DateTime.UtcNow + maxTotalRetryDuration.Value;
            }

            TimeSpan nextRetryDelay = initialRetryDelay;
            while (true)
            {
                bool isLastCall = lastCallAt.HasValue && lastCallAt < DateTime.UtcNow;
                try
                {
                    T result = callback.Invoke();
                    if (responseValidator.Invoke(result))
                    {
                        return result;
                    } else if (isLastCall)
                    {
                        throw new RetryLimitExceededException();
                    }
                }
                catch (Exception e)
                {
                    if (isLastCall || !errorValidator.Invoke(e))
                    {
                        throw e;
                    }
                }

                Thread.Sleep(nextRetryDelay);
                nextRetryDelay = TimeSpan.FromMilliseconds(nextRetryDelay.TotalMilliseconds * 2);
                if (maxRetryDelay.HasValue && nextRetryDelay.TotalMilliseconds > maxRetryDelay.Value.TotalMilliseconds)
                {
                    nextRetryDelay = maxRetryDelay.Value;
                }
            }
        }
    }

    [Serializable]
    public class RetryLimitExceededException : Exception
    {
        public RetryLimitExceededException() { }
    }
}
