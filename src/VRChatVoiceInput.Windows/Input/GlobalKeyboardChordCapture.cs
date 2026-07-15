using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRChatVoiceInput.Windows.Input;

public sealed class GlobalKeyboardChordCapture : IAsyncDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;

    private readonly HookProcedure _hookProcedure;
    private readonly KeyboardChordAccumulator _accumulator = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<KeyboardChordCapture> _captured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> _keysHeldWhenStarted = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private bool _armed;

    public GlobalKeyboardChordCapture()
    {
        _hookProcedure = HookCallback;
    }

    public static async Task<KeyboardChordCapture> CaptureAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Capture timeout must be greater than zero.");
        }

        await using var capture = new GlobalKeyboardChordCapture();
        await capture.StartAsync(cancellationToken);
        try
        {
            return await capture._captured.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"No keyboard shortcut was completed within {timeout.TotalSeconds:F0} seconds.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Keyboard shortcut capture is already started.");
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Global keyboard shortcut capture"
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

        if (_stopped.Task.IsCompleted)
        {
            await _stopped.Task.WaitAsync(cancellationToken);
            _thread = null;
            return;
        }

        if (_started.Task.IsFaulted)
        {
            await _stopped.Task.WaitAsync(cancellationToken);
            _thread = null;
            return;
        }

        if (_threadId != 0 && !PostThreadMessage(_threadId, WmQuit, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop keyboard capture thread.");
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
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, moduleHandle, 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install keyboard capture hook.");
            }

            RecordInitiallyHeldKeys();
            _armed = _keysHeldWhenStarted.Count == 0;
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
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var messageId = unchecked((uint)message.ToInt64());
        var isDown = messageId is WmKeyDown or WmSysKeyDown;
        var isUp = messageId is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        if (!_armed)
        {
            if (isUp)
            {
                RemoveInitiallyHeldKey(Marshal.ReadInt32(data));
                _armed = _keysHeldWhenStarted.Count == 0;
            }

            return CallNextHookEx(_hook, code, message, data);
        }

        var virtualKey = Marshal.ReadInt32(data);
        var keys = _accumulator.Process(virtualKey, isDown);
        if (keys is not null)
        {
            _captured.TrySetResult(new KeyboardChordCapture(keys));
        }

        return 1;
    }

    private void RecordInitiallyHeldKeys()
    {
        for (var virtualKey = 8; virtualKey <= 255; virtualKey++)
        {
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                _keysHeldWhenStarted.Add(virtualKey);
            }
        }
    }

    private void RemoveInitiallyHeldKey(int virtualKey)
    {
        _keysHeldWhenStarted.Remove(virtualKey);
        switch (virtualKey)
        {
            case 0x10:
                _keysHeldWhenStarted.Remove(0xA0);
                _keysHeldWhenStarted.Remove(0xA1);
                break;
            case 0x11:
                _keysHeldWhenStarted.Remove(0xA2);
                _keysHeldWhenStarted.Remove(0xA3);
                break;
            case 0x12:
                _keysHeldWhenStarted.Remove(0xA4);
                _keysHeldWhenStarted.Remove(0xA5);
                break;
            case 0xA0:
            case 0xA1:
                _keysHeldWhenStarted.Remove(0x10);
                break;
            case 0xA2:
            case 0xA3:
                _keysHeldWhenStarted.Remove(0x11);
                break;
            case 0xA4:
            case 0xA5:
                _keysHeldWhenStarted.Remove(0x12);
                break;
        }
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}

public sealed class KeyboardChordAccumulator
{
    private readonly List<int> _capturedKeys = new();
    private readonly HashSet<int> _pressedKeys = new();

    public IReadOnlyList<int>? Process(int virtualKey, bool isDown)
    {
        if (virtualKey is < 1 or > 255)
        {
            return null;
        }

        if (isDown)
        {
            if (_pressedKeys.Add(virtualKey) && !_capturedKeys.Contains(virtualKey))
            {
                _capturedKeys.Add(virtualKey);
            }

            return null;
        }

        _pressedKeys.Remove(virtualKey);
        return _capturedKeys.Count > 0 && _pressedKeys.Count == 0
            ? _capturedKeys.ToArray()
            : null;
    }
}

public sealed record KeyboardChordCapture(IReadOnlyList<int> VirtualKeys);
