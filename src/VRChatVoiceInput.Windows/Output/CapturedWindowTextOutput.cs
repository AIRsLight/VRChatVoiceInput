using System.ComponentModel;
using System.Runtime.InteropServices;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Output;

namespace VRChatVoiceInput.Windows.Output;

public sealed class CapturedWindowTextOutput : ITextOutput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyAlt = 0x12;
    private const ushort VirtualKeyV = 0x56;
    private const int ClipboardConsumptionDelayMs = 300;

    private readonly WindowsOutputConfiguration _configuration;

    public CapturedWindowTextOutput(WindowsOutputConfiguration configuration)
    {
        var supportedMethods = new[] { "clipboard-paste", "unicode-send-input", "keyboard" };
        if (!supportedMethods.Contains(configuration.TextInputMethod, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Windows text input method '{configuration.TextInputMethod}' is not implemented.");
        }

        if (configuration.OpenInputDelayMs is < 0 or > 5000)
        {
            throw new InvalidOperationException("Windows open-input delay must be between 0 and 5000 ms.");
        }

        _configuration = configuration;
    }

    public TextOutputTarget CaptureTarget()
    {
        var application = ForegroundApplicationInspector.GetCurrent();
        if (application.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("Select the target application before pressing PTT.");
        }

        return new TextOutputTarget(
            "captured-window",
            application.DisplayName,
            application.WindowHandle,
            application.ProcessId);
    }

    public async Task SendAsync(
        string text,
        TextOutputTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(target);

        var openInputKeys = GetHotkeyKeys(_configuration.OpenInput, "open-input");
        if (openInputKeys.Count > 0)
        {
            SendHotkey(openInputKeys);
            if (_configuration.OpenInputDelayMs > 0)
            {
                await Task.Delay(_configuration.OpenInputDelayMs, cancellationToken);
            }

            ValidateTarget(target);
        }

        switch (_configuration.TextInputMethod.ToLowerInvariant())
        {
            case "clipboard-paste":
                await SendClipboardTextAsync(text, cancellationToken);
                break;
            case "unicode-send-input":
                SendUnicodeText(text);
                break;
            case "keyboard":
                SendKeyboardText(text, target.NativeWindowHandle);
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var submissionKeys = GetSubmissionKeys();
        if (submissionKeys.Count > 0)
        {
            SendHotkey(submissionKeys);
        }
    }

    private void ValidateTarget(TextOutputTarget target)
    {
        if (target.NativeWindowHandle == 0 || !IsWindow(target.NativeWindowHandle))
        {
            throw new InvalidOperationException("The captured target window no longer exists.");
        }

        GetWindowThreadProcessId(target.NativeWindowHandle, out var currentProcessId);
        if (currentProcessId != unchecked((uint)target.ProcessId))
        {
            throw new InvalidOperationException("The captured window handle now belongs to another process.");
        }

        if (_configuration.RequireSameForeground &&
            ForegroundApplicationInspector.GetCurrent().WindowHandle != target.NativeWindowHandle)
        {
            throw new InvalidOperationException("Focus changed during recognition; text injection was cancelled.");
        }
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        SendInputs(inputs);
    }

    private static void SendKeyboardText(string text, nint targetWindowHandle)
    {
        var targetThreadId = GetWindowThreadProcessId(targetWindowHandle, out _);
        var keyboardLayout = GetKeyboardLayout(targetThreadId);
        var inputs = new List<Input>(text.Length * 6);
        foreach (var character in text)
        {
            var keyAndModifiers = VkKeyScanEx(character, keyboardLayout);
            if (keyAndModifiers == -1)
            {
                throw new InvalidOperationException(
                    $"Character '{character}' cannot be typed with the target keyboard layout. " +
                    "Use clipboard or Unicode input instead.");
            }

            var virtualKey = unchecked((ushort)(keyAndModifiers & 0xFF));
            var modifiers = (keyAndModifiers >> 8) & 0xFF;
            AddModifierInputs(inputs, modifiers, keyUp: false);
            inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: false));
            inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: true));
            AddModifierInputs(inputs, modifiers, keyUp: true);
        }

        SendInputs(inputs);
    }

    private static void AddModifierInputs(List<Input> inputs, int modifiers, bool keyUp)
    {
        var keys = new List<ushort>(3);
        if ((modifiers & 1) != 0)
        {
            keys.Add(VirtualKeyShift);
        }

        if ((modifiers & 2) != 0)
        {
            keys.Add(VirtualKeyControl);
        }

        if ((modifiers & 4) != 0)
        {
            keys.Add(VirtualKeyAlt);
        }

        if (keyUp)
        {
            keys.Reverse();
        }

        inputs.AddRange(keys.Select(key => CreateVirtualKeyInput(key, keyUp)));
    }

    private static Task SendClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Forms.IDataObject? previousClipboard = null;
            Exception? failure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                previousClipboard = CaptureClipboardSnapshot();
                RetryClipboardOperation(() => System.Windows.Forms.Clipboard.SetText(text));
                SendHotkey([VirtualKeyControl, VirtualKeyV]);
                Thread.Sleep(ClipboardConsumptionDelayMs);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    if (previousClipboard is null)
                    {
                        RetryClipboardOperation(System.Windows.Forms.Clipboard.Clear);
                    }
                    else
                    {
                        RetryClipboardOperation(() =>
                            System.Windows.Forms.Clipboard.SetDataObject(previousClipboard, copy: true));
                    }
                }
                catch (ExternalException)
                {
                    // Text was already pasted; clipboard restoration failure must not duplicate output.
                }

                if (failure is null)
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
        })
        {
            IsBackground = true,
            Name = "VRChatVoiceInput.Clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static T RetryClipboardOperation<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }

    private static void RetryClipboardOperation(Action operation) =>
        RetryClipboardOperation(() =>
        {
            operation();
            return true;
        });

    private static System.Windows.Forms.IDataObject? CaptureClipboardSnapshot() =>
        RetryClipboardOperation(() =>
        {
            var current = System.Windows.Forms.Clipboard.GetDataObject();
            if (current is null)
            {
                return null;
            }

            var snapshot = new System.Windows.Forms.DataObject();
            foreach (var format in current.GetFormats(autoConvert: false))
            {
                var data = current.GetData(format, autoConvert: false);
                if (data is not null)
                {
                    snapshot.SetData(format, autoConvert: false, data);
                }
            }

            return snapshot;
        });

    private IReadOnlyList<int> GetSubmissionKeys()
    {
        if (_configuration.PressEnterAfterInjection &&
            string.Equals(_configuration.Submission.Mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return [VirtualKeyReturn];
        }

        return GetHotkeyKeys(_configuration.Submission, "submission");
    }

    private static IReadOnlyList<int> GetHotkeyKeys(SubmissionConfiguration configuration, string description)
    {
        if (string.Equals(configuration.Mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<int>();
        }

        if (!string.Equals(configuration.Mode, "hotkey", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported Windows {description} mode '{configuration.Mode}'.");
        }

        var keys = configuration.VirtualKeys;
        if (keys.Count == 0 || keys.Any(key => key is < 1 or > 255) || keys.Distinct().Count() != keys.Count)
        {
            throw new InvalidOperationException(
                $"Windows {description} hotkey must contain unique virtual keys between 1 and 255.");
        }

        return keys;
    }

    private static void SendHotkey(IReadOnlyList<int> keys)
    {
        var inputs = new List<Input>(keys.Count * 2);
        foreach (var virtualKey in keys)
        {
            inputs.Add(CreateVirtualKeyInput(unchecked((ushort)virtualKey), keyUp: false));
        }

        for (var index = keys.Count - 1; index >= 0; index--)
        {
            inputs.Add(CreateVirtualKeyInput(unchecked((ushort)keys[index]), keyUp: true));
        }

        SendInputs(inputs);
    }

    private static void SendInputs(IReadOnlyList<Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var inputArray = inputs.ToArray();
        var sent = SendInput(unchecked((uint)inputArray.Length), inputArray, Marshal.SizeOf<Input>());
        if (sent != inputArray.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Only {sent} of {inputArray.Length} keyboard events were injected.");
        }
    }

    private static Input CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
            }
        }
    };

    private static Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanEx(char character, nint keyboardLayout);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
