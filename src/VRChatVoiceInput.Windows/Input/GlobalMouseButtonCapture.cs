using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRChatVoiceInput.Windows.Input;

public sealed class GlobalMouseButtonCapture : IAsyncDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;

    private readonly HookProcedure _hookProcedure;
    private readonly MouseButtonCaptureAccumulator _accumulator = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<MouseButtonCapture> _captured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;

    public GlobalMouseButtonCapture()
    {
        _hookProcedure = HookCallback;
    }

    public static async Task<MouseButtonCapture> CaptureAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Capture timeout must be greater than zero.");
        }

        await using var capture = new GlobalMouseButtonCapture();
        await capture.StartAsync(cancellationToken);
        try
        {
            return await capture._captured.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"No mouse button was completed within {timeout.TotalSeconds:F0} seconds.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Mouse button capture is already started.");
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Global mouse button capture"
        };
        _thread.Start();
        await _started.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is null)
        {
            return;
        }

        if (!_started.Task.IsCompleted && !_stopped.Task.IsCompleted)
        {
            await Task.WhenAny(_started.Task, _stopped.Task).WaitAsync(cancellationToken);
        }

        if (_stopped.Task.IsCompleted || _started.Task.IsFaulted)
        {
            await _stopped.Task.WaitAsync(cancellationToken);
            _thread = null;
            return;
        }

        if (_threadId != 0 && !PostThreadMessage(_threadId, WmQuit, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop mouse capture thread.");
        }

        await _stopped.Task.WaitAsync(cancellationToken);
        _thread = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleHandle = GetModuleHandle(module?.ModuleName);
            _hook = SetWindowsHookEx(WhMouseLl, _hookProcedure, moduleHandle, 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install mouse capture hook.");
            }

            _started.TrySetResult();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            _captured.TrySetException(exception);
        }
        finally
        {
            if (_hook != 0)
            {
                UnhookWindowsHookEx(_hook);
                _hook = 0;
            }

            _stopped.TrySetResult();
        }
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code < 0 || !MouseHookEvent.TryCreate(unchecked((uint)message.ToInt64()), data, out var mouseEvent))
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var button = _accumulator.Process(mouseEvent.Button, mouseEvent.IsDown);
        if (button is not null)
        {
            _captured.TrySetResult(new MouseButtonCapture(button));
        }

        return 1;
    }

    private delegate nint HookProcedure(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, HookProcedure callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}

public sealed class MouseButtonCaptureAccumulator
{
    private string? _pressedButton;

    public string? Process(string button, bool isDown)
    {
        var normalized = MousePttButtons.Normalize(button);
        if (isDown)
        {
            _pressedButton ??= normalized;
            return null;
        }

        return string.Equals(_pressedButton, normalized, StringComparison.Ordinal)
            ? _pressedButton
            : null;
    }
}

public sealed record MouseButtonCapture(string Button);
