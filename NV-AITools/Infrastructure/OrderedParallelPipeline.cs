using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace NVAITools.Infrastructure;

static class OrderedParallelPipeline
{
    public const int MaximumConcurrency = 16;

    public static async Task<TResult[]> RunAsync<TSource, TPrepared, TResult>(
        IReadOnlyList<TSource> source,
        Func<int, TSource, CancellationToken, Task<TPrepared>> prepare,
        Func<int, TPrepared, CancellationToken, Task<TResult>> transform,
        CancellationToken cancellationToken)
    {
        if (source.Count == 0)
            return [];

        var channel = Channel.CreateBounded<Indexed<TPrepared>>(
            new BoundedChannelOptions(MaximumConcurrency)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
        var results = new TResult[source.Count];
        int workerCount = Math.Min(MaximumConcurrency, source.Count);
        int nextIndex = -1;
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

        async Task PrepareAsync()
        {
            try
            {
                while (true)
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= source.Count)
                        return;

                    TPrepared prepared = await prepare(index, source[index], stopping.Token);
                    await channel.Writer.WriteAsync(new Indexed<TPrepared>(index, prepared), stopping.Token);
                }
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
                await foreach (Indexed<TPrepared> item in channel.Reader.ReadAllAsync(stopping.Token))
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    results[item.Index] = await transform(item.Index, item.Value, stopping.Token);
                }
            }
            catch (Exception exception)
            {
                Stop(exception);
            }
        }

        var producers = new Task[workerCount];
        var consumers = new Task[workerCount];
        for (int index = 0; index < workerCount; index++)
        {
            producers[index] = PrepareAsync();
            consumers[index] = TransformAsync();
        }

        await Task.WhenAll(producers);
        channel.Writer.TryComplete();
        await Task.WhenAll(consumers);

        cancellationToken.ThrowIfCancellationRequested();
        failure?.Throw();
        return results;
    }

    readonly record struct Indexed<T>(int Index, T Value);
}
