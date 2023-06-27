using System;

namespace Centrifuge
{
    public class Backoff
    {
        public int Duration(int step, int minDelay, int maxDelay)
        {
            // Full jitter technique.
            // https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
            double currentStep = step;
            if (currentStep > 31)
            {
                currentStep = 31;
            }

            double min = Math.Min(maxDelay, minDelay * Math.Pow(2, currentStep));
            int val = (int)(new Random().NextDouble() * (min + 1));
            double duration = Math.Min(maxDelay, minDelay + val);

            return duration > int.MaxValue ? int.MaxValue : (int)duration;
        }
    }
}