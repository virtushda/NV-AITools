using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace NVAITools.Infrastructure;

readonly record struct IndexedItem<T>(int Index, T Value);

static class OrderedBatchPipeline
{
    public const int MaximumConcurrency = 16;

    public static async Task<TResult[]> RunAsync<TBatch, TPrepared, TResult>(
        IReadOnlyList<TBatch> batches,
        int resultCount,
        Func<TBatch, CancellationToken, Task<IReadOnlyList<IndexedItem<TPrepared>>>> prepareBatch,
        Func<int, TPrepared, CancellationToken, Task<TResult>> transform,
        CancellationToken cancellationToken)
    {
        if (resultCount == 0)
        {
            if (batches.Count != 0)
                throw new InvalidOperationException("A batch pipeline with no results cannot contain batches.");
            return [];
        }

        var channel = Channel.CreateBounded<IndexedItem<TPrepared>>(
            new BoundedChannelOptions(MaximumConcurrency)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
        var results = new TResult[resultCount];
        var produced = new bool[resultCount];
        int producedCount = 0;
        ExceptionDispatchInfo? failure = null;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Stop(Exception exception)
        {
            var captured = ExceptionDispatchInfo.Capture(exception);
            if (Interlocked.CompareExchange(ref failure, captured, null) is not null)
                return;

            stopping.Cancel();
            channel.Writer.TryComplete(exception);
        }

        async Task ProduceAsync()
        {
            try
            {
                for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    IReadOnlyList<IndexedItem<TPrepared>> items = await prepareBatch(
                        batches[batchIndex],
                        stopping.Token);

                    for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
                    {
                        int index = items[itemIndex].Index;
                        if ((uint)index >= (uint)results.Length)
                            throw new InvalidOperationException($"Prepared result index {index} is out of range.");
                        if (produced[index])
                            throw new InvalidOperationException($"Prepared result index {index} was produced more than once.");
                        produced[index] = true;
                        producedCount++;
                    }

                    for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
                        await channel.Writer.WriteAsync(items[itemIndex], stopping.Token);
                }

                if (producedCount != results.Length)
                    throw new InvalidOperationException(
                        $"Batch preparation produced {producedCount} of {results.Length} required results.");
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                Stop(exception);
            }
        }

        async Task TransformAsync()
        {
            try
            {
                await foreach (IndexedItem<TPrepared> item in channel.Reader.ReadAllAsync(stopping.Token))
                    results[item.Index] = await transform(item.Index, item.Value, stopping.Token);
            }
            catch (Exception exception)
            {
                Stop(exception);
            }
        }

        Task producer = ProduceAsync();
        int workerCount = Math.Min(MaximumConcurrency, resultCount);
        var consumers = new Task[workerCount];
        for (int index = 0; index < consumers.Length; index++)
            consumers[index] = TransformAsync();

        await producer;
        await Task.WhenAll(consumers);

        cancellationToken.ThrowIfCancellationRequested();
        failure?.Throw();
        return results;
    }
}
