using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;

namespace VRChatVoiceInput.Windows.Input;

public sealed class GlobalMousePttInput : IPushToTalkInput, IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;

    private readonly MouseInputConfiguration _configuration;
    private readonly string _button;
    private readonly Func<bool> _isActive;
    private readonly HookProcedure _hookProcedure;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private bool _isPressed;

    public GlobalMousePttInput(MouseInputConfiguration configuration, Func<bool>? isActive = null)
    {
        _configuration = configuration;
        _button = MousePttButtons.Normalize(configuration.Button);
        _isActive = isActive ?? (() => true);
        _hookProcedure = HookCallback;
    }

    public event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Mouse PTT input is already started.");
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Global mouse PTT hook"
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop mouse hook thread.");
        }

        await _stopped.Task.WaitAsync(cancellationToken);
        _thread = null;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install global mouse hook.");
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
        if (code < 0 ||
            !MouseHookEvent.TryCreate(unchecked((uint)message.ToInt64()), data, out var mouseEvent) ||
            !string.Equals(mouseEvent.Button, _button, StringComparison.Ordinal))
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var bindingIsActive = _isPressed || _isActive();
        SetPressed(mouseEvent.IsDown && bindingIsActive);
        if (_configuration.SuppressButton && bindingIsActive)
        {
            return 1;
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private void SetPressed(bool pressed)
    {
        if (_isPressed == pressed)
        {
            return;
        }

        _isPressed = pressed;
        StateChanged?.Invoke(this, new PushToTalkChangedEventArgs(pressed, DateTimeOffset.UtcNow));
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

public static class MousePttButtons
{
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmMiddleButtonUp = 0x0208;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;

    public const string Left = "left";
    public const string Right = "right";
    public const string Middle = "middle";
    public const string X1 = "x1";
    public const string X2 = "x2";

    public static string Normalize(string? button)
    {
        var normalized = button?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Left or Right or Middle or X1 or X2 => normalized,
            _ => throw new ArgumentOutOfRangeException(
                nameof(button),
                button,
                "Mouse PTT button must be left, right, middle, x1, or x2.")
        };
    }

    public static bool TryDecodeWindowsEvent(
        uint message,
        uint mouseData,
        out string button,
        out bool isDown)
    {
        string? decodedButton = message switch
        {
            WmLeftButtonDown or WmLeftButtonUp => Left,
            WmRightButtonDown or WmRightButtonUp => Right,
            WmMiddleButtonDown or WmMiddleButtonUp => Middle,
            WmXButtonDown or WmXButtonUp => (mouseData >> 16) switch
            {
                1 => X1,
                2 => X2,
                _ => null
            },
            _ => null
        };
        if (decodedButton is null)
        {
            button = string.Empty;
            isDown = false;
            return false;
        }

        button = decodedButton;
        isDown = message is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmXButtonDown;
        return true;
    }
}

internal readonly record struct MouseHookEvent(string Button, bool IsDown)
{
    public static bool TryCreate(uint message, nint data, out MouseHookEvent mouseEvent)
    {
        var hookData = Marshal.PtrToStructure<LowLevelMouseInput>(data);
        if (!MousePttButtons.TryDecodeWindowsEvent(
                message,
                hookData.MouseData,
                out var button,
                out var isDown))
        {
            mouseEvent = default;
            return false;
        }

        mouseEvent = new MouseHookEvent(button, isDown);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
