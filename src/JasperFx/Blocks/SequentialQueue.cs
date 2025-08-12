using System.Diagnostics;
using System.Threading.Channels;
using JasperFx.Core;

namespace JasperFx.Blocks;

public class SequentialQueue<T> : IAsyncDisposable
{
    private readonly Func<T, CancellationToken, Task> _action;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _task;
    private bool _latched;

    public SequentialQueue(Func<T, CancellationToken, Task> action)
    {
        _action = action;
        
        // TODO -- what about on dropping????????
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(10000)
        {
            SingleReader = true,
            SingleWriter = false
        });

        _task = Task.Run(processAsync, _cancellation.Token);
    }

    private Action<T, Exception> _onError = (item, ex) =>
    {
        Debug.WriteLine("Error processing item " + item);
        Debug.WriteLine(ex.ToString());
    };

    public Action<T, Exception> OnError
    {
        get => _onError;
        set => _onError = value ?? throw new ArgumentNullException(nameof(OnError));
    }

    public async Task WaitForCompletionAsync()
    {
        _latched = true;

        if (_channel.Reader.Count == 0) return;
        
        await _task;

        while (!_cancellation.IsCancellationRequested && _channel.Reader.Count > 0)
        {
            var item = await _channel.Reader.ReadAsync(_cancellation.Token);

            try
            {
                await _action(item, _cancellation.Token);
            }
            catch (Exception e)
            {
                _onError(item, e);
            }
        }
    }

    private async Task processAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            await _channel.Reader.WaitToReadAsync();

            if (_channel.Reader.TryRead(out var item))
            {
                try
                {
                    await _action(item, _cancellation.Token);
                }
                catch (Exception e)
                {
                    _onError(item, e);
                }
            }

            if (_latched && _channel.Reader.Count == 0) return;
        }
    }

    public ValueTask PostAsync(T item)
    {
        if (_latched) throw new InvalidOperationException("This SequentialQueue is latched");
        
        return _channel.Writer.WriteAsync(item, _cancellation.Token);
    }
    
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.SafeDispose();
        
        _task.SafeDispose();   
    }
}