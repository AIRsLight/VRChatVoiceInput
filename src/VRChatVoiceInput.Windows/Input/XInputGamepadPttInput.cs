using System.Runtime.InteropServices;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;

namespace VRChatVoiceInput.Windows.Input;

public sealed class XInputGamepadPttInput : IPushToTalkInput, IDisposable
{
    private const uint ErrorSuccess = 0;
    private const int ReleaseDebounceMilliseconds = 60;
    private readonly GamepadInputConfiguration _configuration;
    private readonly Func<bool> _isActive;
    private CancellationTokenSource? _cancellation;
    private Task? _pollTask;
    private bool _isPressed;
    private bool _wasPhysicallyPressed;
    private long? _releaseCandidateTimestamp;

    public XInputGamepadPttInput(GamepadInputConfiguration configuration, Func<bool>? isActive = null)
    {
        if (configuration.UserIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "userIndex must be between 0 and 3.");
        }

        if (configuration.ButtonMask is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "buttonMask must be a valid XInput button mask.");
        }

        if (configuration.PollIntervalMs is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "pollIntervalMs must be between 1 and 1000.");
        }

        _configuration = configuration;
        _isActive = isActive ?? (() => true);
    }

    public event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_pollTask is not null)
        {
            throw new InvalidOperationException("XInput PTT input is already started.");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_pollTask is null || _cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        try
        {
            await _pollTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }

        _cancellation.Dispose();
        _cancellation = null;
        _pollTask = null;
        _releaseCandidateTimestamp = null;
        _wasPhysicallyPressed = false;
        SetPressed(false);
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public static IReadOnlyList<GamepadDeviceInfo> ListConnectedControllers()
    {
        var devices = new List<GamepadDeviceInfo>();
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetState(index, out _) == ErrorSuccess)
            {
                devices.Add(new GamepadDeviceInfo(unchecked((int)index), $"XInput controller {index + 1}"));
            }
        }

        return devices;
    }

    public static async Task<GamepadButtonCapture> CaptureButtonAsync(
        int userIndex,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (userIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex), "Controller index must be between 0 and 3.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Capture timeout must be greater than zero.");
        }

        if (XInputGetState(unchecked((uint)userIndex), out var initialState) != ErrorSuccess)
        {
            throw new InvalidOperationException($"XInput controller {userIndex + 1} is not connected.");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        var previousButtons = initialState.Gamepad.Buttons;
        var isArmed = previousButtons == 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (XInputGetState(unchecked((uint)userIndex), out var state) != ErrorSuccess)
            {
                throw new InvalidOperationException($"XInput controller {userIndex + 1} was disconnected.");
            }

            var buttons = state.Gamepad.Buttons;
            if (!isArmed)
            {
                isArmed = buttons == 0;
            }
            else
            {
                var newlyPressed = unchecked((ushort)(buttons & ~previousButtons));
                if (newlyPressed != 0)
                {
                    return new GamepadButtonCapture(userIndex, newlyPressed);
                }
            }

            previousButtons = buttons;
            await Task.Delay(8, cancellationToken);
        }

        throw new TimeoutException($"No button was pressed on XInput controller {userIndex + 1} within {timeout.TotalSeconds:F0} seconds.");
    }

    public static async Task<GamepadButtonCapture> CaptureButtonAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Capture timeout must be greater than zero.");
        }

        var previousButtons = new ushort[4];
        var armed = new bool[4];
        var connected = new bool[4];
        var connectedCount = 0;
        for (var index = 0; index < 4; index++)
        {
            if (XInputGetState(unchecked((uint)index), out var state) != ErrorSuccess)
            {
                continue;
            }

            connected[index] = true;
            connectedCount++;
            previousButtons[index] = state.Gamepad.Buttons;
            armed[index] = previousButtons[index] == 0;
        }

        if (connectedCount == 0)
        {
            throw new InvalidOperationException("No connected XInput controller was found.");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            connectedCount = 0;
            for (var index = 0; index < 4; index++)
            {
                if (XInputGetState(unchecked((uint)index), out var state) != ErrorSuccess)
                {
                    connected[index] = false;
                    continue;
                }

                connectedCount++;
                var buttons = state.Gamepad.Buttons;
                if (!connected[index])
                {
                    connected[index] = true;
                    previousButtons[index] = buttons;
                    armed[index] = buttons == 0;
                    continue;
                }

                if (!armed[index])
                {
                    armed[index] = buttons == 0;
                }
                else
                {
                    var newlyPressed = unchecked((ushort)(buttons & ~previousButtons[index]));
                    if (newlyPressed != 0)
                    {
                        return new GamepadButtonCapture(index, newlyPressed);
                    }
                }

                previousButtons[index] = buttons;
            }

            if (connectedCount == 0)
            {
                throw new InvalidOperationException("All XInput controllers were disconnected.");
            }

            await Task.Delay(8, cancellationToken);
        }

        throw new TimeoutException($"No XInput controller button was pressed within {timeout.TotalSeconds:F0} seconds.");
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = XInputGetState(unchecked((uint)_configuration.UserIndex), out var state) == ErrorSuccess;
            var physicallyPressed = connected &&
                                    (state.Gamepad.Buttons & _configuration.ButtonMask) == _configuration.ButtonMask;
            UpdatePhysicalState(physicallyPressed);
            await Task.Delay(_configuration.PollIntervalMs, cancellationToken);
        }
    }

    private void UpdatePhysicalState(bool physicallyPressed)
    {
        if (physicallyPressed)
        {
            _releaseCandidateTimestamp = null;
            if (!_isPressed && !_wasPhysicallyPressed && _isActive())
            {
                SetPressed(true);
            }

            _wasPhysicallyPressed = true;
            return;
        }

        _wasPhysicallyPressed = false;
        if (!_isPressed)
        {
            _releaseCandidateTimestamp = null;
            return;
        }

        var now = Environment.TickCount64;
        _releaseCandidateTimestamp ??= now;
        if (now - _releaseCandidateTimestamp.Value >= ReleaseDebounceMilliseconds)
        {
            _releaseCandidateTimestamp = null;
            SetPressed(false);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}

public sealed record GamepadDeviceInfo(int UserIndex, string Name);

public sealed record GamepadButtonCapture(int UserIndex, int ButtonMask);
