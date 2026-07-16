using VRChatVoiceInput.Core.Input;

namespace VRChatVoiceInput.Windows.Input;

public sealed class CompositePushToTalkInput : IPushToTalkInput, IDisposable
{
    private readonly IReadOnlyList<IPushToTalkInput> _inputs;
    private readonly Dictionary<IPushToTalkInput, bool> _pressedStates;
    private readonly object _sync = new();
    private bool _started;
    private bool _isPressed;

    public CompositePushToTalkInput(IEnumerable<IPushToTalkInput> inputs)
    {
        _inputs = inputs.ToArray();
        if (_inputs.Count == 0)
        {
            throw new ArgumentException("At least one push-to-talk input is required.", nameof(inputs));
        }

        if (_inputs.Distinct().Count() != _inputs.Count)
        {
            throw new ArgumentException("Push-to-talk inputs must be unique.", nameof(inputs));
        }

        _pressedStates = _inputs.ToDictionary(input => input, _ => false);
    }

    public event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("Composite PTT input is already started.");
        }

        var startedInputs = new List<IPushToTalkInput>();
        foreach (var input in _inputs)
        {
            input.StateChanged += OnInputStateChanged;
        }

        try
        {
            foreach (var input in _inputs)
            {
                await input.StartAsync(cancellationToken);
                startedInputs.Add(input);
            }

            _started = true;
        }
        catch
        {
            foreach (var input in startedInputs.AsEnumerable().Reverse())
            {
                try
                {
                    await input.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the startup exception while making a best effort to stop earlier inputs.
                }
            }

            foreach (var input in _inputs)
            {
                input.StateChanged -= OnInputStateChanged;
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        Exception? failure = null;
        foreach (var input in _inputs.Reverse())
        {
            try
            {
                await input.StopAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                input.StateChanged -= OnInputStateChanged;
            }
        }

        lock (_sync)
        {
            foreach (var input in _inputs)
            {
                _pressedStates[input] = false;
            }

            _isPressed = false;
            _started = false;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var input in _inputs.OfType<IDisposable>())
        {
            input.Dispose();
        }
    }

    private void OnInputStateChanged(object? sender, PushToTalkChangedEventArgs eventArgs)
    {
        if (sender is not IPushToTalkInput input)
        {
            return;
        }

        bool nextState;
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _pressedStates[input] = eventArgs.IsPressed;
            nextState = _pressedStates.Values.Any(isPressed => isPressed);
            if (nextState == _isPressed)
            {
                return;
            }

            _isPressed = nextState;
        }

        StateChanged?.Invoke(this, new PushToTalkChangedEventArgs(nextState, eventArgs.Timestamp));
    }
}
