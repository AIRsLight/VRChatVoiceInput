using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;

namespace VRChatVoiceInput.Windows.Input;

public sealed class GlobalKeyboardPttInput : IPushToTalkInput, IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;

    private readonly KeyboardInputConfiguration _configuration;
    private readonly HashSet<int> _virtualKeys;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly Func<bool> _isActive;
    private readonly HookProcedure _hookProcedure;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private bool _isPressed;

    public GlobalKeyboardPttInput(KeyboardInputConfiguration configuration, Func<bool>? isActive = null)
    {
        var virtualKeys = configuration.VirtualKeys.Count > 0
            ? configuration.VirtualKeys
            : [configuration.VirtualKey];
        if (virtualKeys.Count == 0 || virtualKeys.Any(key => key is < 1 or > 255))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Every keyboard PTT virtual key must be between 1 and 255.");
        }

        if (virtualKeys.Distinct().Count() != virtualKeys.Count)
        {
            throw new ArgumentException("Keyboard PTT virtual keys must be unique.", nameof(configuration));
        }

        _configuration = configuration;
        _virtualKeys = virtualKeys.ToHashSet();
        _isActive = isActive ?? (() => true);
        _hookProcedure = HookCallback;
    }

    public event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Keyboard PTT input is already started.");
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Global keyboard PTT hook"
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

        if (_threadId != 0 && !PostThreadMessage(_threadId, WmQuit, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to stop keyboard hook thread.");
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
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, moduleHandle, 0);
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install global keyboard hook.");
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
        var virtualKey = code >= 0 ? Marshal.ReadInt32(data) : 0;
        var configuredVirtualKey = ResolveConfiguredVirtualKey(virtualKey);
        if (code >= 0 && configuredVirtualKey is not null)
        {
            var messageId = unchecked((uint)message.ToInt64());
            var isDown = messageId is WmKeyDown or WmSysKeyDown;
            var isUp = messageId is WmKeyUp or WmSysKeyUp;
            var bindingIsActive = _isPressed || _isActive();

            if (isDown)
            {
                _pressedKeys.Add(virtualKey);
            }
            else if (isUp)
            {
                _pressedKeys.Remove(virtualKey);
            }

            var chordIsPressed = _virtualKeys.All(IsConfiguredKeyPressed);
            SetPressed(bindingIsActive && chordIsPressed);

            if (_configuration.SuppressKey && bindingIsActive && (isDown || isUp))
            {
                return 1;
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private int? ResolveConfiguredVirtualKey(int virtualKey)
    {
        if (_virtualKeys.Contains(virtualKey))
        {
            return virtualKey;
        }

        var genericModifier = virtualKey switch
        {
            0xA0 or 0xA1 => 0x10,
            0xA2 or 0xA3 => 0x11,
            0xA4 or 0xA5 => 0x12,
            _ => 0
        };
        return genericModifier != 0 && _virtualKeys.Contains(genericModifier)
            ? genericModifier
            : null;
    }

    private bool IsConfiguredKeyPressed(int virtualKey)
    {
        if (_pressedKeys.Contains(virtualKey))
        {
            return true;
        }

        return virtualKey switch
        {
            0x10 => _pressedKeys.Contains(0xA0) || _pressedKeys.Contains(0xA1),
            0x11 => _pressedKeys.Contains(0xA2) || _pressedKeys.Contains(0xA3),
            0x12 => _pressedKeys.Contains(0xA4) || _pressedKeys.Contains(0xA5),
            _ => false
        };
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
