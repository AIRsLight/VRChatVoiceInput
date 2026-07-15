using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VRChatVoiceInput.App;

public partial class MainWindow
{
    private const string RuntimeSwitchTestDirectoryVariable = "VRCHAT_VOICE_INPUT_RUNTIME_SWITCH_TEST_DIRECTORY";
    private const string RuntimeSwitchTestCasesVariable = "VRCHAT_VOICE_INPUT_RUNTIME_SWITCH_TEST_CASES";
    private static readonly TimeSpan RuntimeSwitchTimeout = TimeSpan.FromMinutes(2);

    private void StartRuntimeSwitchTestIfRequested()
    {
        var directory = Environment.GetEnvironmentVariable(RuntimeSwitchTestDirectoryVariable);
        if (_runtimeSwitchTestStarted || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        _runtimeSwitchTestStarted = true;
        _ = RunRuntimeSwitchTestAsync(Path.GetFullPath(directory));
    }

    private async Task RunRuntimeSwitchTestAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var results = new List<RuntimeSwitchTestResult>();
        var cases = new[]
        {
            new RuntimeSwitchTestCase("paraformer", "paraformer-gguf", "paraformer", null, null),
            new RuntimeSwitchTestCase("sensevoice-cpu", "sensevoice-gguf", "senseVoice", "sensevoice", false),
            new RuntimeSwitchTestCase("sensevoice-vulkan", "sensevoice-gguf", "senseVoice", "sensevoice", true),
            new RuntimeSwitchTestCase("funasr-nano-cpu", "funasr-nano-gguf", "funAsrNano", "nano", false),
            new RuntimeSwitchTestCase("funasr-nano-vulkan", "funasr-nano-gguf", "funAsrNano", "nano", true),
            new RuntimeSwitchTestCase("qwen3-asr", "qwen3-asr", "qwen3Asr", null, null),
            new RuntimeSwitchTestCase("whisper-cpu", "whisper-cpp", "whisperCpp", "whisper", false),
            new RuntimeSwitchTestCase("whisper-vulkan", "whisper-cpp", "whisperCpp", "whisper", true)
        };
        var requestedCases = (Environment.GetEnvironmentVariable(RuntimeSwitchTestCasesVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedCases.Count > 0)
        {
            cases = cases.Where(testCase => requestedCases.Contains(testCase.Name)).ToArray();
        }

        try
        {
            for (var index = 0; index < cases.Length; index++)
            {
                var testCase = cases[index];
                var startedAt = DateTimeOffset.Now;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var runtimeWasStopped = await StopRuntimeIfRunningAsync(RuntimeSwitchTimeout);
                    await ConfigureRuntimeSwitchCaseAsync(testCase);
                    await WaitForSavedConfigurationAsync(testCase, RuntimeSwitchTimeout);
                    await EnsureRuntimeRunningAsync(RuntimeSwitchTimeout);
                    await WaitForRuntimeEvidenceAsync(
                        testCase,
                        startedAt,
                        requireStopped: runtimeWasStopped,
                        RuntimeSwitchTimeout);

                    await SelectModelTabAsync(testCase.ModelTab);
                    await Task.Delay(250);
                    await CapturePreviewAsync(outputDirectory, $"runtime-{testCase.Name}");
                    stopwatch.Stop();
                    var evidence = ReadRuntimeEvidence(testCase, startedAt);
                    results.Add(new RuntimeSwitchTestResult(
                        testCase.Name,
                        testCase.ProviderId,
                        testCase.GpuEnabled,
                        true,
                        stopwatch.ElapsedMilliseconds,
                        evidence.WorkingSetBytes,
                        evidence.Stopped,
                        evidence.Ready,
                        evidence.Started,
                        null));
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    AppFileLogger.Error(
                        "runtime-switch-test",
                        $"Runtime switch test '{testCase.Name}' failed.",
                        exception);
                    _runtimeController.AddHostLog(
                        "error",
                        $"Runtime switch test '{testCase.Name}' failed: {exception.Message}",
                        exception: exception);
                    var evidence = ReadRuntimeEvidence(testCase, startedAt);
                    results.Add(new RuntimeSwitchTestResult(
                        testCase.Name,
                        testCase.ProviderId,
                        testCase.GpuEnabled,
                        false,
                        stopwatch.ElapsedMilliseconds,
                        evidence.WorkingSetBytes,
                        evidence.Stopped,
                        evidence.Ready,
                        evidence.Started,
                        exception.Message));
                }
            }
        }
        catch (Exception exception)
        {
            AppFileLogger.Error("runtime-switch-test", "Runtime switch test harness failed.", exception);
        }
        finally
        {
            var resultPath = Path.Combine(outputDirectory, "runtime-switch-results.json");
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            await Dispatcher.InvokeAsync(Close);
        }
    }

    private async Task ConfigureRuntimeSwitchCaseAsync(RuntimeSwitchTestCase testCase)
    {
        if (testCase.GpuTarget is not null && testCase.GpuEnabled is not null)
        {
            var enabled = testCase.GpuEnabled.Value ? "true" : "false";
            var togglePath = testCase.GpuTarget switch
            {
                "sensevoice" => "asr.senseVoice.backend",
                "nano" => "asr.funAsrNano.backend",
                _ => "asr.whisperCpp.useGpu"
            };
            await ExecuteRequiredUiScriptAsync(
                "const models = document.querySelector('[data-view=\"models\"]'); if (!models) return 'models view missing'; models.click();" +
                $"const tab = document.querySelector('[data-model-tab=\"{testCase.ModelTab}\"]');" +
                "if (!tab) return 'model tab missing: ' + Array.from(document.querySelectorAll('[data-model-tab]')).map(item => item.dataset.modelTab).join(','); tab.click();" +
                $"const toggle = Array.from(document.querySelectorAll('input[data-config-path]')).find(item => item.dataset.configPath === '{togglePath}');" +
                $"if (!toggle) return 'GPU toggle missing: ' + Array.from(document.querySelectorAll('[data-config-path]')).map(item => item.dataset.configPath).join(','); if (toggle.checked !== {enabled}) toggle.click(); return true;",
                $"set {testCase.Name} GPU mode");
        }

        var provider = JsonSerializer.Serialize(testCase.ProviderId);
        await ExecuteRequiredUiScriptAsync(
            "const profiles = document.querySelector('[data-view=\"profiles\"]'); if (!profiles) return false; profiles.click();" +
            "const profile = document.querySelector('[data-profile-id=\"Desktop default\"]'); if (profile) profile.click();" +
            "const processing = document.querySelector('[data-profile-tab=\"processing\"]'); if (!processing) return false; processing.click();" +
            "const select = document.querySelector('select[data-config-path$=\".recognition.provider\"]');" +
            $"if (!select || !Array.from(select.options).some(option => option.value === {provider})) return false;" +
            $"if (select.value !== {provider}) {{ select.value = {provider}; select.dispatchEvent(new Event('change', {{ bubbles: true }})); }} return true;",
            $"select provider {testCase.ProviderId}");
    }

    private async Task WaitForSavedConfigurationAsync(
        RuntimeSwitchTestCase testCase,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var configuration = _runtimeController.LoadConfiguration();
            var profile = configuration.GetEffectiveProfiles().First(profile => profile.Id == "Desktop default");
            var providerMatches = string.Equals(
                profile.Recognition.Provider,
                testCase.ProviderId,
                StringComparison.OrdinalIgnoreCase);
            var gpuMatches = testCase.GpuTarget switch
            {
                "sensevoice" => string.Equals(
                    configuration.Asr.SenseVoice.Backend,
                    testCase.GpuEnabled == true ? "vulkan" : "cpu",
                    StringComparison.OrdinalIgnoreCase),
                "whisper" => configuration.Asr.WhisperCpp.UseGpu == testCase.GpuEnabled,
                "nano" => string.Equals(
                    configuration.Asr.FunAsrNano.Backend,
                    testCase.GpuEnabled == true ? "vulkan" : "cpu",
                    StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            var uiSaved = await IsUiSaveCompleteAsync();
            if (providerMatches && gpuMatches && uiSaved)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out while saving runtime switch '{testCase.Name}'.");
    }

    private async Task<bool> StopRuntimeIfRunningAsync(TimeSpan timeout)
    {
        if (!_runtimeController.IsRunning)
        {
            return false;
        }

        await ExecuteRequiredUiScriptAsync(
            "const button = document.querySelector('#runtime-toggle'); if (!button || button.disabled) return false; button.click(); return true;",
            "stop runtime");

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_runtimeController.IsRunning)
            {
                return true;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Timed out while waiting for the runtime to stop.");
    }

    private async Task EnsureRuntimeRunningAsync(TimeSpan timeout)
    {
        if (!_runtimeController.IsRunning)
        {
            await ExecuteRequiredUiScriptAsync(
                "const button = document.querySelector('#runtime-toggle'); if (!button || button.disabled) return false; button.click(); return true;",
                "start runtime");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_runtimeController.IsRunning && await IsUiSaveCompleteAsync())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Timed out while waiting for the runtime to start.");
    }

    private async Task WaitForRuntimeEvidenceAsync(
        RuntimeSwitchTestCase testCase,
        DateTimeOffset startedAt,
        bool requireStopped,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var evidence = ReadRuntimeEvidence(testCase, startedAt);
            if ((!requireStopped || evidence.Stopped) && evidence.Ready && evidence.Started)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Runtime logs did not confirm loading '{testCase.Name}'.");
    }

    private RuntimeSwitchEvidence ReadRuntimeEvidence(
        RuntimeSwitchTestCase testCase,
        DateTimeOffset startedAt)
    {
        var snapshot = _runtimeController.GetRuntimeDiagnosticSnapshot();
        var logs = snapshot.Logs
            .Where(entry => entry.Timestamp >= startedAt)
            .ToArray();
        return new RuntimeSwitchEvidence(
            snapshot.WorkingSetBytes,
            logs.Any(entry => string.Equals(entry.Code, "stopped", StringComparison.OrdinalIgnoreCase)),
            logs.Any(entry =>
                string.Equals(entry.Code, "ready", StringComparison.OrdinalIgnoreCase) &&
                entry.Message.Contains(testCase.ProviderId, StringComparison.OrdinalIgnoreCase)),
            logs.Any(entry => string.Equals(entry.Code, "started", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<bool> IsUiSaveCompleteAsync()
    {
        var result = await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('.autosave-status')?.classList.contains('saved') === true");
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SelectModelTabAsync(string modelTab)
    {
        var tab = JsonSerializer.Serialize(modelTab);
        await ExecuteRequiredUiScriptAsync(
            "const models = document.querySelector('[data-view=\"models\"]'); if (!models) return false; models.click();" +
            $"const tab = document.querySelector('[data-model-tab=\"{modelTab}\"]'); if (!tab) return false; tab.click();" +
            $"return document.querySelector('.model-tab.active')?.dataset.modelTab === {tab};",
            $"open model tab {modelTab}");
    }

    private async Task ExecuteRequiredUiScriptAsync(string body, string operation)
    {
        var result = await WebView.CoreWebView2.ExecuteScriptAsync($"(() => {{ {body} }})()");
        if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"UI automation could not {operation}. Script result: {result}");
        }
    }

    private sealed record RuntimeSwitchTestCase(
        string Name,
        string ProviderId,
        string ModelTab,
        string? GpuTarget,
        bool? GpuEnabled);

    private sealed record RuntimeSwitchEvidence(
        long WorkingSetBytes,
        bool Stopped,
        bool Ready,
        bool Started);

    private sealed record RuntimeSwitchTestResult(
        string Name,
        string ProviderId,
        bool? GpuEnabled,
        bool Passed,
        long ElapsedMilliseconds,
        long WorkingSetBytes,
        bool StoppedObserved,
        bool ReadyObserved,
        bool StartedObserved,
        string? Error);
}
