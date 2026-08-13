using System.Threading;

namespace DshGUI.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\DshGUI.SingleInstance";
    private const string EventName = @"Local\DshGUI.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;

    public SingleInstanceService()
    {
        _mutex = new Mutex(false, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirstInstance { get; }

    public void SignalExistingInstance() => _showEvent.Set();

    public void Listen(Action onShow)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    _showEvent.WaitOne();
                    onShow();
                }
            }
            catch
            {
                // 进程退出时 EventWaitHandle 被释放，正常结束。
            }
        })
        {
            IsBackground = true,
            Name = "DshGUI.SingleInstanceListener",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _showEvent.Dispose();
        _mutex.Dispose();
    }
}
