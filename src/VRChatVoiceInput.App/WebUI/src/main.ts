import { resolveUiLocale, translate, type UiLocale } from "./i18n";
import { gamepadButtonNames } from "./gamepad";
import { materialIcon, type MaterialIconName } from "./materialIcons";
import "./styles.css";

interface ApplicationSettings {
  settingsInterface: string;
  uiLanguage: string;
  startRuntimeOnLaunch: boolean;
  closeToTray: boolean;
  startWithWindows: boolean;
}

interface AudioSettings {
  deviceId: string | null;
  minimumDurationMs: number;
}

interface ProfileAudioSettings {
  deviceId: string | null;
  minimumDurationMs: number | null;
}

interface KeyboardSettings {
  virtualKey?: number;
  virtualKeys: number[];
  suppressKey: boolean;
}

interface MouseSettings {
  button: string;
  suppressButton: boolean;
}

interface GamepadSettings {
  userIndex: number;
  buttonMask: number;
  pollIntervalMs: number;
}

interface InputSettings {
  mode: string;
  keyboard: KeyboardSettings;
  mouse: MouseSettings;
  gamepad: GamepadSettings;
  steamVr: { actionPath: string; pollIntervalMs: number };
}

interface RecognitionSettings {
  provider: string;
  language: string;
  hotwords: string[];
  streamingEnabled: boolean;
}

interface SubmissionSettings {
  mode: string;
  virtualKeys: number[];
}

interface WindowsOutputSettings {
  textInputMethod: string;
  openInput: SubmissionSettings;
  openInputDelayMs: number;
  requireSameForeground: boolean;
  pressEnterAfterInjection?: boolean;
  submission: SubmissionSettings;
}

interface VrChatOutputSettings {
  host: string;
  port: number;
  sendImmediately: boolean;
  maxChatboxCharacters: number;
}

interface OutputSettings {
  mode: string;
  windows: WindowsOutputSettings;
  vrChat: VrChatOutputSettings;
}

interface Profile {
  id: string;
  displayName?: string;
  builtInTemplate?: string;
  enabled: boolean;
  builtIn: boolean;
  match: { processNames: string[] };
  audio: ProfileAudioSettings;
  input: InputSettings;
  recognition: RecognitionSettings;
  output: OutputSettings;
}

interface ProfilesSettings {
  defaultProfileId: string;
  items: Profile[];
}

interface AsrSettings {
  provider: string;
  senseVoice: {
    backend: string;
    cpuExecutablePath: string;
    vulkanExecutablePath: string;
    vulkanDeviceName: string | null;
    vulkanDeviceIndex: number | null;
    modelPath: string;
    vadModelPath: string | null;
  };
  paraformer: {
    executablePath: string;
    modelPath: string;
    vadModelPath: string | null;
    usePunctuation: boolean;
    punctuationModelPath: string;
  };
  funAsrNano: {
    backend: string;
    executablePath: string;
    vulkanExecutablePath: string;
    vulkanDeviceName: string | null;
    vulkanDeviceIndex: number | null;
    encoderModelPath: string;
    languageModelPath: string;
    vadModelPath: string | null;
    chunkSeconds: number;
  };
  qwen3Asr: {
    convFrontendPath: string;
    encoderPath: string;
    decoderPath: string;
    tokenizerPath: string;
    threadCount: number;
    maxNewTokens: number;
  };
  streaming: {
    sileroVadModelPath: string;
    threshold: number;
    minimumSilenceSeconds: number;
    minimumSpeechSeconds: number;
    maximumSegmentSeconds: number;
  };
  whisperCpp: {
    executablePath: string;
    vulkanExecutablePath: string;
    serverExecutablePath: string;
    vulkanServerExecutablePath: string;
    modelPath: string;
    language: string;
    threadCount: number;
    useGpu: boolean;
    gpuDeviceIndex: number | null;
    vadModelPath: string | null;
  };
}

interface AppConfiguration {
  schemaVersion: number;
  application: ApplicationSettings;
  asr: AsrSettings;
  audio: AudioSettings;
  profiles: ProfilesSettings;
}

interface RuntimeLog {
  timestamp: string;
  code: string;
  message: string;
  profileId: string | null;
  recognitionDurationMilliseconds: number | null;
}

interface DiagnosticMetrics {
  workingSetBytes: number;
  averageRecognitionMilliseconds: number | null;
  recognitionCount: number;
}

interface ProviderStatus {
  id: string;
  available: boolean;
  missingFiles: string[];
  supportsStreaming: boolean;
  streamingAvailable: boolean;
  streamingMissingFiles: string[];
  supportsTerminologyHints: boolean;
}

interface SteamVrStatus {
  runtimeInstalled: boolean;
  connected: boolean;
  message: string;
}

interface GpuDevice {
  index: number;
  name: string;
  backend: string;
  vendorId: number;
  deviceId: number;
  deviceType: string;
}

interface MicrophoneLevel {
  id: string;
  name: string;
  level: number;
  decibels: number;
  available: boolean;
  error: string | null;
}

interface RunningApplication {
  processId: number;
  processName: string;
  windowTitle: string;
  displayName: string;
}

interface ModelDownloadProgress {
  providerId: string;
  state: "checking" | "downloading" | "verifying" | "extracting" | "completed" | "canceled" | "error";
  fileName: string | null;
  bytesDownloaded: number;
  totalBytes: number;
  message: string;
}

interface ModelAssetStatus {
  id: string;
  providerId: string;
  componentId: string;
  variant: string;
  targetPath: string;
  size: number;
  installed: boolean;
  updateAvailable: boolean;
}

interface Snapshot {
  configuration: AppConfiguration;
  runtime: { isRunning: boolean; profileOverride: string | null };
  environment: {
    configurationPath: string;
    logFilePath: string;
    applicationVersion: string;
    repositoryUrl: string;
    webViewVersion: string;
  };
  microphones: Array<{ id: string; name: string }>;
  runningApplications: RunningApplication[];
  gpuDevices: GpuDevice[];
  microphoneTest: { isRunning: boolean; levels: MicrophoneLevel[] };
  providerStatuses: ProviderStatus[];
  modelAssets: ModelAssetStatus[];
  modelDownload: ModelDownloadProgress | null;
  steamVr: SteamVrStatus;
  diagnostics: DiagnosticMetrics;
  logs: RuntimeLog[];
}

interface BridgeResponse<T> {
  version: number;
  id: string;
  type: string;
  ok: boolean;
  payload: T;
  error?: { code: string; message: string };
}

interface NativeCloseState {
  dirty: boolean;
  saveInProgress: boolean;
  busy: boolean;
  configuration: AppConfiguration | null;
}

type NativeLifecycleWindow = Window & {
  vrchatVoiceInputPrepareClose?: () => string;
  vrchatVoiceInputCancelClose?: () => void;
};

type ViewId = "general" | "profiles" | "models" | "diagnostics";
type ProfileTab = "input" | "processing" | "output";

const modelProviderIds: Record<string, string> = {
  paraformer: "paraformer-gguf",
  senseVoice: "sensevoice-gguf",
  funAsrNano: "funasr-nano-gguf",
  qwen3Asr: "qwen3-asr",
  whisperCpp: "whisper-cpp"
};

type ParaformerQuantization = "q8_0" | "q5_0" | "q4_0";

const paraformerVariants: Record<ParaformerQuantization, {
  assetId: string;
  label: string;
  modelPath: string;
  fileSize: string;
  peakRam: string;
  charactersPerSecond: number;
  description: string;
}> = {
  q5_0: {
    assetId: "paraformer-q5_0",
    label: "Q5_0",
    modelPath: "models/paraformer-q5_0.gguf",
    fileSize: "149.7 MiB",
    peakRam: "176.5 MiB",
    charactersPerSecond: 124,
    description: "Recommended balance"
  },
  q8_0: {
    assetId: "paraformer-q8_0",
    label: "Q8_0",
    modelPath: "models/paraformer-q8.gguf",
    fileSize: "226.0 MiB",
    peakRam: "252.8 MiB",
    charactersPerSecond: 148,
    description: "Higher fidelity"
  },
  q4_0: {
    assetId: "paraformer-q4_0",
    label: "Q4_0",
    modelPath: "models/paraformer-q4_0.gguf",
    fileSize: "124.3 MiB",
    peakRam: "151.1 MiB",
    charactersPerSecond: 172,
    description: "Lowest memory"
  }
};

function getParaformerQuantization(modelPath: string): ParaformerQuantization | "custom" {
  const normalized = modelPath.replaceAll("\\", "/").split("/").at(-1)?.toLowerCase();
  if (normalized === "paraformer-q8.gguf") return "q8_0";
  if (normalized === "paraformer-q5_0.gguf") return "q5_0";
  if (normalized === "paraformer-q4_0.gguf") return "q4_0";
  return "custom";
}

type SenseVoiceQuantization = "q8_0" | "q5_0";

const senseVoiceVariants: Record<SenseVoiceQuantization, {
  assetId: string;
  label: string;
  modelPath: string;
  fileSize: string;
  peakRam: string;
  charactersPerSecond: number;
  description: string;
}> = {
  q8_0: {
    assetId: "sensevoice-q8_0",
    label: "Q8_0",
    modelPath: "models/sensevoice-small-q8.gguf",
    fileSize: "242.4 MiB",
    peakRam: "358.8 MiB",
    charactersPerSecond: 135,
    description: "Higher fidelity"
  },
  q5_0: {
    assetId: "sensevoice-q5_0",
    label: "Q5_0",
    modelPath: "models/sensevoice-small-q5_0.gguf",
    fileSize: "159.4 MiB",
    peakRam: "275.8 MiB",
    charactersPerSecond: 120,
    description: "Low memory"
  }
};

function getSenseVoiceQuantization(modelPath: string): SenseVoiceQuantization | "custom" {
  const normalized = modelPath.replaceAll("\\", "/").split("/").at(-1)?.toLowerCase();
  if (normalized === "sensevoice-small-q8.gguf") return "q8_0";
  if (normalized === "sensevoice-small-q5_0.gguf") return "q5_0";
  return "custom";
}

type NanoLanguageModelQuantization = "q4_k_m" | "q5_k_m" | "q8_0";

const nanoLanguageModelVariants: Record<NanoLanguageModelQuantization, {
  assetId: string;
  label: string;
  modelPath: string;
  fileSize: string;
  description: string;
}> = {
  q4_k_m: {
    assetId: "nano-language-q4_k_m",
    label: "Q4_K_M",
    modelPath: "models/qwen3-0.6b-q4km.gguf",
    fileSize: "461.8 MiB",
    description: "Recommended balance"
  },
  q5_k_m: {
    assetId: "nano-language-q5_k_m",
    label: "Q5_K_M",
    modelPath: "models/qwen3-0.6b-q5km.gguf",
    fileSize: "525.8 MiB",
    description: "Best upstream accuracy"
  },
  q8_0: {
    assetId: "nano-language-q8_0",
    label: "Q8_0",
    modelPath: "models/qwen3-0.6b-q8_0.gguf",
    fileSize: "767.5 MiB",
    description: "Higher precision"
  }
};

const nanoCombinationBenchmarks: Record<string, { peakRam: string; charactersPerSecond: number }> = {
  "nano-encoder-q8_0:nano-language-q4_k_m": { peakRam: "1131.5 MiB", charactersPerSecond: 64 },
  "nano-encoder-f16:nano-language-q4_k_m": { peakRam: "1337.5 MiB", charactersPerSecond: 51 }
};

function getNanoLanguageModelQuantization(modelPath: string): NanoLanguageModelQuantization | "custom" {
  const normalized = modelPath.replaceAll("\\", "/").split("/").at(-1)?.toLowerCase();
  if (normalized === "qwen3-0.6b-q4km.gguf") return "q4_k_m";
  if (normalized === "qwen3-0.6b-q5km.gguf") return "q5_k_m";
  if (normalized === "qwen3-0.6b-q8_0.gguf") return "q8_0";
  return "custom";
}

const appRoot = document.querySelector<HTMLDivElement>("#app");
if (!appRoot) {
  throw new Error("Application root was not found.");
}
const root: HTMLDivElement = appRoot;

let snapshot: Snapshot | null = null;
let configuration: AppConfiguration | null = null;
let activeView: ViewId = "general";
let selectedProfileId = "";
let activeProfileTab: ProfileTab = "input";
let selectedModel = "paraformer";
let advancedModelConfigurationOpen = false;
const selectedModelVariants: Record<string, string> = {};
const selectedCapabilityVariants: Record<string, string> = {};
let modelDownload: ModelDownloadProgress | null = null;
let uiLocale: UiLocale = resolveUiLocale("auto");
let dirty = false;
let busy = false;
let saveInProgress = false;
let configurationRevision = 0;
let autoSaveFailureCount = 0;
let autoSaveTimer: number | null = null;
let keyboardCapturePath: string | null = null;
let mouseCaptureProfileId: string | null = null;
let gamepadCaptureProfileId: string | null = null;
let processPickerProfileId: string | null = null;
let processPickerOptions: RunningApplication[] = [];
let processPickerSelection = new Set<string>();
let processPickerLoading = false;
let microphoneTestRunning = false;
let microphoneLevels: MicrophoneLevel[] = [];
let diagnosticProfileId = "";
let diagnosticTargetProcessId: number | null = null;
let diagnosticMetricsTimer: number | null = null;
let requestSequence = 0;
const pendingRequests = new Map<
  string,
  { resolve: (value: unknown) => void; reject: (reason: Error) => void }
>();

function bridgeRequest<T>(type: string, payload: object = {}): Promise<T> {
  const id = `ui-${Date.now()}-${++requestSequence}`;
  return new Promise<T>((resolve, reject) => {
    pendingRequests.set(id, {
      resolve: value => resolve(value as T),
      reject
    });
    window.chrome.webview.postMessage({ version: 1, id, type, payload });
  });
}

function reportWebUiError(message: string, stack?: string): void {
  try {
    window.chrome.webview.postMessage({
      version: 1,
      id: `ui-error-${Date.now()}-${++requestSequence}`,
      type: "ui.error",
      payload: { message, stack: stack ?? null }
    });
  } catch {
    // Native logging may be unavailable if WebView2 itself has failed.
  }
}

window.addEventListener("error", event => {
  reportWebUiError(event.message || "Unhandled web UI error.", event.error?.stack);
});

window.addEventListener("unhandledrejection", event => {
  const reason = event.reason;
  reportWebUiError(
    reason instanceof Error ? reason.message : String(reason ?? "Unhandled promise rejection."),
    reason instanceof Error ? reason.stack : undefined);
});

window.chrome.webview.addEventListener("message", event => {
  const message = event.data as BridgeResponse<unknown> & { payload: unknown };
  if (message.id && pendingRequests.has(message.id)) {
    const pending = pendingRequests.get(message.id)!;
    pendingRequests.delete(message.id);
    if (message.ok) {
      pending.resolve(message.payload);
    } else {
      pending.reject(new Error(message.error?.message ?? "Native request failed."));
    }
    return;
  }

  if (!snapshot) {
    return;
  }

  if (message.type === "runtime.log") {
    snapshot.logs.push(message.payload as RuntimeLog);
    snapshot.logs = snapshot.logs.slice(-300);
    if (activeView === "diagnostics") {
      render();
    }
  } else if (message.type === "runtime.state") {
    snapshot.runtime.isRunning = (message.payload as { isRunning: boolean }).isRunning;
    updateTopbar();
  } else if (message.type === "model.download.progress") {
    modelDownload = message.payload as ModelDownloadProgress;
    snapshot.modelDownload = modelDownload;
    if (activeView === "models") {
      render();
    }
  } else if (message.type === "microphone.levels" && microphoneTestRunning) {
    microphoneLevels = message.payload as MicrophoneLevel[];
    if (activeView === "general") {
      updateMicrophoneMeters();
    }
  }
});

function escapeHtml(value: unknown): string {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function deepClone<T>(value: T): T {
  return structuredClone(value);
}

function t(key: string, parameters: Record<string, string | number> = {}): string {
  return translate(uiLocale, key, parameters);
}

function applyUiLanguage(setting: string): void {
  uiLocale = resolveUiLocale(setting);
  document.documentElement.lang = uiLocale === "zh" ? "zh-CN" : uiLocale === "ja" ? "ja-JP" : "en";
}

function normalizeConfiguration(config: AppConfiguration): AppConfiguration {
  config.application.settingsInterface ??= "native-wpf";
  if (!["webview", "native-wpf"].includes(config.application.settingsInterface)) {
    config.application.settingsInterface = "native-wpf";
  }
  config.application.uiLanguage ??= "auto";
  if (!["auto", "zh", "ja", "en"].includes(config.application.uiLanguage)) {
    config.application.uiLanguage = "en";
  }
  config.asr.whisperCpp.vulkanExecutablePath ??= "runtimes/whispercpp-vulkan/Release/whisper-cli.exe";
  config.asr.whisperCpp.serverExecutablePath ??= "runtimes/whispercpp/Release/whisper-server.exe";
  config.asr.whisperCpp.vulkanServerExecutablePath ??= "runtimes/whispercpp-vulkan/Release/whisper-server.exe";
  config.asr.whisperCpp.gpuDeviceIndex ??= null;
  config.asr.senseVoice.backend ??= "cpu";
  if (!["cpu", "vulkan"].includes(config.asr.senseVoice.backend)) {
    config.asr.senseVoice.backend = "cpu";
  }
  config.asr.senseVoice.vulkanExecutablePath ??= "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe";
  config.asr.senseVoice.vulkanDeviceName ??= null;
  config.asr.senseVoice.vulkanDeviceIndex ??= null;
  config.asr.funAsrNano.backend ??= "cpu";
  if (!["cpu", "vulkan"].includes(config.asr.funAsrNano.backend)) {
    config.asr.funAsrNano.backend = "cpu";
  }
  config.asr.funAsrNano.vulkanExecutablePath ??= "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe";
  config.asr.funAsrNano.vulkanDeviceName ??= null;
  config.asr.funAsrNano.vulkanDeviceIndex ??= null;
  config.asr.paraformer.usePunctuation ??= false;
  config.asr.paraformer.punctuationModelPath ??= "models/paraformer-punctuation-int8.onnx";
  config.asr.qwen3Asr ??= {
    convFrontendPath: "models/qwen3-asr/conv_frontend.onnx",
    encoderPath: "models/qwen3-asr/encoder.int8.onnx",
    decoderPath: "models/qwen3-asr/decoder.int8.onnx",
    tokenizerPath: "models/qwen3-asr/tokenizer",
    threadCount: 4,
    maxNewTokens: 256
  };
  config.asr.streaming ??= {
    sileroVadModelPath: "models/silero_vad.onnx",
    threshold: 0.5,
    minimumSilenceSeconds: 0.5,
    minimumSpeechSeconds: 0.25,
    maximumSegmentSeconds: 15
  };
  for (const profile of config.profiles.items) {
    delete profile.displayName;
    profile.audio ??= { deviceId: null, minimumDurationMs: null };
    profile.audio.minimumDurationMs ??= null;
    profile.input.keyboard.virtualKeys ??= [profile.input.keyboard.virtualKey ?? 119];
    profile.input.mouse ??= { button: "x1", suppressButton: false };
    profile.input.steamVr ??= { actionPath: "/actions/voiceinput/in/ptt", pollIntervalMs: 8 };
    profile.input.steamVr.pollIntervalMs ??= 8;
    profile.recognition.hotwords ??= [];
    profile.recognition.streamingEnabled ??= false;
    profile.output.windows ??= {
      textInputMethod: "clipboard-paste",
      openInput: { mode: "none", virtualKeys: [] },
      openInputDelayMs: 100,
      requireSameForeground: true,
      submission: { mode: "none", virtualKeys: [] }
    };
    profile.output.windows.openInput ??= { mode: "none", virtualKeys: [] };
    profile.output.windows.openInputDelayMs ??= 100;
    profile.output.windows.submission ??= { mode: "none", virtualKeys: [] };
    profile.output.vrChat ??= {
      host: "127.0.0.1",
      port: 9000,
      sendImmediately: true,
      maxChatboxCharacters: 144
    };
  }
  return config;
}

function getProfile(): Profile {
  if (!configuration) {
    throw new Error("Configuration is unavailable.");
  }
  return configuration.profiles.items.find(profile => profile.id === selectedProfileId)
    ?? configuration.profiles.items[0];
}

function getProviderStatus(providerId: string): ProviderStatus {
  return snapshot!.providerStatuses.find(status => status.id === providerId)
    ?? {
      id: providerId,
      available: false,
      missingFiles: ["Provider is unknown"],
      supportsStreaming: false,
      streamingAvailable: false,
      streamingMissingFiles: ["Streaming provider is unknown"],
      supportsTerminologyHints: false
    };
}

function pathSegments(path: string): string[] {
  return path.split(".").filter(Boolean);
}

function setPath(path: string, value: unknown): void {
  if (!configuration) {
    return;
  }
  const segments = pathSegments(path);
  let target = configuration as unknown as Record<string, unknown>;
  for (let index = 0; index < segments.length - 1; index++) {
    target = target[segments[index]] as Record<string, unknown>;
  }
  const key = segments.at(-1)!;
  if (configValuesEqual(target[key], value)) {
    return;
  }
  target[key] = value;
  markDirty();
}

function configValuesEqual(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) {
    return true;
  }
  if (Array.isArray(left) && Array.isArray(right)) {
    return left.length === right.length && left.every((value, index) => Object.is(value, right[index]));
  }
  return false;
}

function markDirty(): void {
  dirty = true;
  configurationRevision++;
  autoSaveFailureCount = 0;
  scheduleAutoSave();
  updateTopbar();
}

function scheduleAutoSave(delay = 700): void {
  if (autoSaveTimer !== null) window.clearTimeout(autoSaveTimer);
  autoSaveTimer = window.setTimeout(() => {
    autoSaveTimer = null;
    void saveConfiguration();
  }, delay);
}

function icon(name: MaterialIconName): string {
  return materialIcon(name);
}

function navButton(view: ViewId, label: string, iconName: MaterialIconName): string {
  return `<button class="nav-button ${activeView === view ? "active" : ""}" data-view="${view}" title="${label}">
    ${icon(iconName)}<span>${label}</span>
  </button>`;
}

function render(): void {
  if (!snapshot || !configuration) {
    root.innerHTML = `<div class="empty-state">${t("Loading...")}</div>`;
    return;
  }

  if (activeView !== "diagnostics" && diagnosticMetricsTimer !== null) {
    window.clearInterval(diagnosticMetricsTimer);
    diagnosticMetricsTimer = null;
  }

  root.innerHTML = `<div class="app-shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">${icon("waveform")}</div>
        <div class="brand-copy">
          <div class="brand-title">${t("Voice Input")}</div>
          <div class="brand-subtitle">${t("VRChat companion")}</div>
        </div>
      </div>
      <nav class="nav">
        ${navButton("general", t("General"), "cogOutline")}
        ${navButton("profiles", t("Profiles"), "accountOutline")}
        ${navButton("models", t("Models"), "memory")}
        ${navButton("diagnostics", t("Diagnostics"), "chartBoxOutline")}
      </nav>
      <div class="sidebar-footer">
        <div>App ${escapeHtml(snapshot.environment.applicationVersion)}</div>
        <div>WebView ${escapeHtml(snapshot.environment.webViewVersion.split(" ")[0])}</div>
        <a class="repository-link" href="${escapeHtml(snapshot.environment.repositoryUrl)}"
           target="_blank" rel="noopener noreferrer" title="${t("Project repository")}">
          ${icon("github")}<span>${t("Project repository")}</span>
        </a>
      </div>
    </aside>
    <main class="main">
      <header class="topbar" id="topbar"></header>
      <div class="content">${renderView()}</div>
    </main>
  </div>
  <div class="toast-region" id="toast-region"></div>
  ${renderProcessPicker()}`;

  updateTopbar();
  bindCommonEvents();
  bindViewEvents();
  bindProcessPickerEvents();
}

function updateTopbar(): void {
  if (!snapshot) {
    return;
  }
  const topbar = document.querySelector<HTMLElement>("#topbar");
  if (!topbar) {
    return;
  }
  const titles: Record<ViewId, [string, string]> = {
    general: [t("General"), snapshot.environment.configurationPath],
    profiles: [t("Application profiles"), selectedProfileId || t("No profile selected")],
    models: [t("Local models"), selectedModel],
    diagnostics: [t("Diagnostics"), t("{count} log entries", { count: snapshot.logs.length })]
  };
  const [title, context] = titles[activeView];
  const running = snapshot.runtime.isRunning;
  const runtimeLabel = snapshot.runtime.profileOverride
    ? `${t(running ? "Running" : "Stopped")} · ${snapshot.runtime.profileOverride}`
    : `${t(running ? "Running" : "Stopped")} · ${t("Automatic")}`;
  topbar.innerHTML = `<div class="page-heading">
      <h1 class="page-title">${escapeHtml(title)}</h1>
      <div class="page-context" title="${escapeHtml(context)}">${escapeHtml(context)}</div>
    </div>
    <div class="runtime-status ${running ? "running" : ""}">
      <span class="status-dot"></span><span>${escapeHtml(runtimeLabel)}</span>
    </div>
    <div class="autosave-status ${saveInProgress ? "saving" : dirty ? "pending" : "saved"}">
      ${icon(saveInProgress ? "refresh" : "check")}<span>${t(saveInProgress ? "Saving..." : dirty ? "Waiting to save" : "Saved")}</span>
    </div>
    <button class="icon-button" id="runtime-toggle" title="${t(running ? "Stop service" : "Start service")}" ${busy ? "disabled" : ""}>
      ${icon(running ? "stop" : "play")}
    </button>`;
  document.querySelector<HTMLButtonElement>("#runtime-toggle")?.addEventListener("click", toggleRuntime);
}

function renderView(): string {
  switch (activeView) {
    case "general":
      return renderGeneral();
    case "profiles":
      return renderProfiles();
    case "models":
      return renderModels();
    case "diagnostics":
      return renderDiagnostics();
  }
}

function toggleControl(
  path: string,
  checked: boolean,
  label: string,
  disabled = false,
  checkedValue?: string,
  uncheckedValue?: string
): string {
  const mappedValues = checkedValue === undefined || uncheckedValue === undefined
    ? ""
    : ` data-checked-value="${escapeHtml(checkedValue)}" data-unchecked-value="${escapeHtml(uncheckedValue)}"`;
  return `<div class="toggle-row">
    <span class="toggle-label">${escapeHtml(label)}</span>
    <label class="toggle">
      <input type="checkbox" data-config-path="${path}"${mappedValues} ${checked ? "checked" : ""} ${disabled ? "disabled" : ""} />
      <span class="toggle-track"></span>
    </label>
  </div>`;
}

function renderGeneral(): string {
  const config = configuration!;
  const microphoneOptions = [
    `<option value="" ${config.audio.deviceId ? "" : "selected"}>${t("Default communications device")}</option>`,
    ...snapshot!.microphones.map(device =>
      `<option value="${escapeHtml(device.id)}" ${device.id === config.audio.deviceId ? "selected" : ""}>${escapeHtml(device.name)}</option>`)
  ].join("");
  const profileOptions = config.profiles.items.map(profile =>
    `<option value="${escapeHtml(profile.id)}" ${profile.id === config.profiles.defaultProfileId ? "selected" : ""}>${escapeHtml(profile.id)}</option>`)
    .join("");

  return `<div class="view">
    <section class="settings-section">
      <div><h2 class="section-title">${t("Service")}</h2><div class="section-meta">${t("Desktop lifecycle")}</div></div>
      <div class="field-stack">
        ${toggleControl("application.startRuntimeOnLaunch", config.application.startRuntimeOnLaunch, t("Start service when the application opens"))}
        ${toggleControl("application.closeToTray", config.application.closeToTray, t("Keep running when the settings window closes"))}
        ${toggleControl("application.startWithWindows", config.application.startWithWindows, t("Start with Windows"))}
      </div>
    </section>
    <section class="settings-section">
      <div><h2 class="section-title">${t("Interface language")}</h2><div class="section-meta">${t("Unsupported system languages fall back to English.")}</div></div>
      <div class="field-stack"><div class="field"><label for="settings-interface">${t("Settings interface")}</label>
        <select class="select" id="settings-interface" data-config-path="application.settingsInterface">
          <option value="webview" ${config.application.settingsInterface === "webview" ? "selected" : ""}>WebView2</option>
          <option value="native-wpf" ${config.application.settingsInterface === "native-wpf" ? "selected" : ""}>Native WPF</option>
        </select>
        <div class="field-help">${t("The selected interface is used the next time settings are opened.")}</div>
      </div><div class="field"><label for="ui-language">${t("Interface language")}</label>
        <select class="select" id="ui-language" data-config-path="application.uiLanguage" data-ui-language="true">
          <option value="auto" ${config.application.uiLanguage === "auto" ? "selected" : ""}>${t("System language")}</option>
          <option value="zh" ${config.application.uiLanguage === "zh" ? "selected" : ""}>${t("Chinese")}</option>
          <option value="ja" ${config.application.uiLanguage === "ja" ? "selected" : ""}>${t("Japanese")}</option>
          <option value="en" ${config.application.uiLanguage === "en" ? "selected" : ""}>${t("English")}</option>
        </select>
      </div></div>
    </section>
    <section class="settings-section">
      <div><h2 class="section-title">${t("Audio")}</h2><div class="section-meta">${t("Shared capture device")}</div></div>
      <div class="field-stack">
        <div class="field">
          <label for="microphone">${t("Microphone")}</label>
          <select class="select" id="microphone" data-config-path="audio.deviceId" data-nullable="true">${microphoneOptions}</select>
        </div>
        <div class="field-row">
          <div class="field">
            <label for="minimum-duration">${t("Minimum recording (ms)")}</label>
            <input class="input" id="minimum-duration" type="number" min="100" max="5000" data-number="true" data-config-path="audio.minimumDurationMs" value="${config.audio.minimumDurationMs}" />
          </div>
          <div class="field">
            <label for="default-profile">${t("Default profile on startup")}</label>
            <select class="select" id="default-profile" data-config-path="profiles.defaultProfileId">${profileOptions}</select>
          </div>
        </div>
        <div class="microphone-test">
          <div class="microphone-test-heading">
            <div><div class="field-label">${t("Microphone test")}</div><div class="field-help">${t("Shows live input from every active microphone.")}</div></div>
            <button class="button ${microphoneTestRunning ? "danger" : ""}" id="microphone-test-toggle">
              ${icon(microphoneTestRunning ? "stop" : "microphone")}
              <span>${t(microphoneTestRunning ? "Stop test" : "Start test")}</span>
            </button>
          </div>
          ${microphoneTestRunning ? renderMicrophoneMeters() : ""}
        </div>
      </div>
    </section>
    <section class="settings-section">
      <div><h2 class="section-title">${t("Configuration")}</h2><div class="section-meta">${t("Schema {version}", { version: config.schemaVersion })}</div></div>
      <div class="field-stack">
        <div class="field">
          <label for="configuration-path">${t("File")}</label>
          <div class="input-with-button">
            <input class="input" id="configuration-path" readonly value="${escapeHtml(snapshot!.environment.configurationPath)}" />
            <button class="icon-button" id="reveal-config" title="${t("Show configuration file")}">${icon("folderOpenOutline")}</button>
          </div>
        </div>
      </div>
    </section>
  </div>`;
}

function renderMicrophoneMeters(): string {
  if (microphoneLevels.length === 0) {
    return `<div class="microphone-empty">${t("No active microphones found.")}</div>`;
  }

  return `<div class="microphone-meters">${microphoneLevels.map((microphone, index) => {
    const percentage = Math.round(Math.max(0, Math.min(1, microphone.level)) * 100);
    const decibels = Number.isFinite(microphone.decibels) ? microphone.decibels.toFixed(1) : "-60.0";
    return `<div class="microphone-meter" data-microphone-index="${index}" data-microphone-id="${escapeHtml(microphone.id)}">
      <div class="microphone-meter-label"><span title="${escapeHtml(microphone.name)}">${escapeHtml(microphone.name)}</span><output>${microphone.available ? `${decibels} dB` : t("Unavailable")}</output></div>
      <div class="level-track" role="meter" aria-label="${escapeHtml(microphone.name)}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${percentage}">
        <span style="width:${percentage}%"></span>
      </div>
    </div>`;
  }).join("")}</div>`;
}

function updateMicrophoneMeters(): void {
  const container = document.querySelector<HTMLElement>(".microphone-meters");
  const rows = [...document.querySelectorAll<HTMLElement>(".microphone-meter")];
  if (!container || rows.length !== microphoneLevels.length || rows.some((row, index) => row.dataset.microphoneId !== microphoneLevels[index]?.id)) {
    render();
    return;
  }

  rows.forEach((row, index) => {
    const microphone = microphoneLevels[index];
    const percentage = Math.round(Math.max(0, Math.min(1, microphone.level)) * 100);
    const fill = row.querySelector<HTMLElement>(".level-track span");
    const track = row.querySelector<HTMLElement>(".level-track");
    const output = row.querySelector<HTMLOutputElement>("output");
    if (fill) fill.style.width = `${percentage}%`;
    if (track) track.setAttribute("aria-valuenow", String(percentage));
    if (output) output.value = microphone.available ? `${microphone.decibels.toFixed(1)} dB` : t("Unavailable");
  });
}

function profileSummary(profile: Profile): string {
  if (profile.match.processNames.length === 0) {
    return t("Current foreground application");
  }
  return profile.match.processNames.join(", ");
}

function normalizeProcessName(value: string): string {
  const fileName = value.trim().split(/[\\/]/).at(-1) ?? "";
  return fileName.replace(/\.exe$/i, "").toLowerCase();
}

function renderProcessPicker(): string {
  if (!processPickerProfileId) return "";

  const options = new Map<string, RunningApplication>();
  for (const application of processPickerOptions) {
    options.set(normalizeProcessName(application.processName), application);
  }
  for (const processName of processPickerSelection) {
    if (!options.has(processName)) {
      options.set(processName, {
        processId: 0,
        processName,
        windowTitle: "",
        displayName: processName
      });
    }
  }

  const rows = [...options.entries()].map(([normalizedName, application]) => {
    const executableName = `${application.processName.replace(/\.exe$/i, "")}.exe`;
    const detail = application.processId > 0
      ? `${application.windowTitle} · PID ${application.processId}`
      : t("Not currently running");
    const search = `${executableName} ${detail}`.toLowerCase();
    return `<label class="process-row" data-process-search="${escapeHtml(search)}">
      <input type="checkbox" value="${escapeHtml(normalizedName)}" ${processPickerSelection.has(normalizedName) ? "checked" : ""} />
      <span><strong>${escapeHtml(executableName)}</strong><small>${escapeHtml(detail)}</small></span>
    </label>`;
  }).join("");

  return `<div class="modal-backdrop" id="process-picker-backdrop">
    <section class="modal process-picker" role="dialog" aria-modal="true" aria-labelledby="process-picker-title">
      <header class="modal-header">
        <div><h2 id="process-picker-title">${t("Choose target processes")}</h2><p>${t("Select one or more running applications for this profile.")}</p></div>
        <button class="icon-button" id="process-picker-close" title="${t("Close")}">${icon("close")}</button>
      </header>
      <div class="process-picker-toolbar">
        <input class="input" id="process-picker-search" placeholder="${t("Search processes")}" ${processPickerLoading ? "disabled" : ""} />
        <button class="icon-button ${processPickerLoading ? "refreshing" : ""}" id="process-picker-refresh" title="${t("Refresh")}" ${processPickerLoading ? "disabled" : ""}>${icon("refresh")}</button>
      </div>
      <div class="process-list" id="process-picker-list">
        ${processPickerLoading ? `<div class="process-picker-state">${t("Loading running applications...")}</div>` : rows || `<div class="process-picker-state">${t("No running applications found")}</div>`}
        <div class="process-picker-state" id="process-picker-no-matches" hidden>${t("No matching processes")}</div>
      </div>
      <footer class="modal-footer">
        <button class="button" id="process-picker-dynamic">${icon("pulse")}<span>${t("Use current foreground")}</span></button>
        <span class="spacer"></span>
        <button class="button" id="process-picker-cancel">${icon("close")}<span>${t("Cancel")}</span></button>
        <button class="button primary" id="process-picker-apply" ${processPickerLoading ? "disabled" : ""}>${icon("check")}<span>${t("Apply")}</span></button>
      </footer>
    </section>
  </div>`;
}

async function openProcessPicker(profile: Profile): Promise<void> {
  processPickerProfileId = profile.id;
  processPickerSelection = new Set(profile.match.processNames.map(normalizeProcessName).filter(Boolean));
  await refreshProcessPicker();
}

async function refreshProcessPicker(): Promise<void> {
  if (!processPickerProfileId) return;
  const requestedProfileId = processPickerProfileId;
  processPickerLoading = true;
  render();
  let failure: unknown = null;
  try {
    const applications = await bridgeRequest<RunningApplication[]>("processes.list");
    if (processPickerProfileId === requestedProfileId) {
      processPickerOptions = applications;
    }
  } catch (error) {
    failure = error;
  } finally {
    if (processPickerProfileId === requestedProfileId) {
      processPickerLoading = false;
      render();
    }
  }
  if (failure) showError(failure);
}

function closeProcessPicker(): void {
  processPickerProfileId = null;
  processPickerOptions = [];
  processPickerSelection.clear();
  processPickerLoading = false;
  render();
}

function applyProcessPicker(useCurrentForeground = false): void {
  const profile = configuration?.profiles.items.find(item => item.id === processPickerProfileId);
  if (!profile) {
    closeProcessPicker();
    return;
  }

  profile.match.processNames = useCurrentForeground
    ? []
    : [...processPickerSelection]
        .sort((left, right) => left.localeCompare(right))
        .map(processName => `${processName}.exe`);
  markDirty();
  processPickerProfileId = null;
  processPickerOptions = [];
  processPickerSelection.clear();
  processPickerLoading = false;
  render();
  showToast(profile.match.processNames.length === 0
    ? t("This profile will use the application in the foreground when PTT is pressed.")
    : t("Target processes updated."));
}

function bindProcessPickerEvents(): void {
  if (!processPickerProfileId) return;

  document.querySelector<HTMLButtonElement>("#process-picker-close")?.addEventListener("click", closeProcessPicker);
  document.querySelector<HTMLButtonElement>("#process-picker-cancel")?.addEventListener("click", closeProcessPicker);
  document.querySelector<HTMLButtonElement>("#process-picker-refresh")?.addEventListener("click", () => void refreshProcessPicker());
  document.querySelector<HTMLButtonElement>("#process-picker-apply")?.addEventListener("click", () => applyProcessPicker());
  document.querySelector<HTMLButtonElement>("#process-picker-dynamic")?.addEventListener("click", () => applyProcessPicker(true));
  document.querySelector<HTMLElement>("#process-picker-backdrop")?.addEventListener("click", event => {
    if (event.target === event.currentTarget) closeProcessPicker();
  });
  document.querySelectorAll<HTMLInputElement>(".process-row input").forEach(input => {
    input.addEventListener("change", () => {
      if (input.checked) processPickerSelection.add(input.value);
      else processPickerSelection.delete(input.value);
    });
  });
  document.querySelector<HTMLInputElement>("#process-picker-search")?.addEventListener("input", event => {
    const query = (event.currentTarget as HTMLInputElement).value.trim().toLowerCase();
    let visibleRows = 0;
    document.querySelectorAll<HTMLElement>(".process-row").forEach(row => {
      const matches = !query || (row.dataset.processSearch ?? "").includes(query);
      row.hidden = !matches;
      if (matches) visibleRows++;
    });
    const noMatches = document.querySelector<HTMLElement>("#process-picker-no-matches");
    if (noMatches) noMatches.hidden = visibleRows > 0;
  });
}

function renderProfiles(): string {
  const config = configuration!;
  const profile = getProfile();
  const index = config.profiles.items.indexOf(profile);
  const base = `profiles.items.${index}`;
  const runtimeProfileId = snapshot!.runtime.profileOverride;
  const providerStatus = getProviderStatus(profile.recognition.provider);
  const recognitionReady = providerStatus.available &&
    (!profile.recognition.streamingEnabled || providerStatus.streamingAvailable);
  const profileItems = config.profiles.items.map(item =>
    `<button class="profile-item ${item.id === profile.id ? "active" : ""} ${item.id === runtimeProfileId ? "runtime-active" : ""}" data-profile-id="${escapeHtml(item.id)}">
      <span><span class="profile-name">${escapeHtml(item.id)}</span><span class="profile-match">${escapeHtml(profileSummary(item))}</span></span>
      <span class="profile-badges">${item.id === runtimeProfileId ? `<span class="profile-badge active">${t("Active")}</span>` : ""}${item.id === config.profiles.defaultProfileId ? `<span class="profile-badge">${t("Default")}</span>` : ""}</span>
    </button>`).join("");

  return `<div class="profile-layout">
    <aside class="profile-list-pane">
      <div class="profile-list-header"><span class="profile-list-title">${t("Profiles")}</span><span class="profile-list-actions">
        <button class="icon-button" id="automatic-routing" title="${t("Resume automatic application routing")}" ${runtimeProfileId ? "" : "disabled"}>${icon("refresh")}</button>
        <button class="icon-button" id="add-profile" title="${t("Add profile")}">${icon("plus")}</button></span>
      </div>
      <div class="profile-list">${profileItems}</div>
    </aside>
    <div class="profile-editor">
      <div class="profile-editor-inner">
        <div class="editor-toolbar">
          <label class="toggle" title="${t("Enable profile")}"><input type="checkbox" data-config-path="${base}.enabled" ${profile.enabled ? "checked" : ""} /><span class="toggle-track"></span></label>
          <button class="button primary" id="use-profile" title="${t(dirty ? "Save changes before activating this profile" : "Activate this profile in the runtime")}" ${profile.id === runtimeProfileId || !profile.enabled || !recognitionReady || dirty ? "disabled" : ""}>${icon("play")}<span>${t("Use profile")}</span></button>
          <button class="button" id="set-default" ${profile.id === config.profiles.defaultProfileId ? "disabled" : ""}>${icon("check")}<span>${t("Set default")}</span></button>
          <span class="routing-label">${runtimeProfileId ? t("Runtime: {profile}", { profile: runtimeProfileId }) : t("Runtime: automatic")}</span>
          <span class="spacer"></span>
          <button class="icon-button" id="duplicate-profile" title="${t("Duplicate profile")}">${icon("contentCopy")}</button>
          <button class="icon-button danger" id="delete-profile" title="${t("Delete profile")}" ${profile.builtIn ? "disabled" : ""}>${icon("deleteOutline")}</button>
        </div>
        <div class="profile-tabs" role="tablist">
          <button class="profile-tab ${activeProfileTab === "input" ? "active" : ""}" data-profile-tab="input">${t("Input")}</button>
          <button class="profile-tab ${activeProfileTab === "processing" ? "active" : ""}" data-profile-tab="processing">${t("Processing")}</button>
          <button class="profile-tab ${activeProfileTab === "output" ? "active" : ""}" data-profile-tab="output">${t("Output")}</button>
        </div>
        <div class="profile-tab-content">
          ${activeProfileTab === "input" ? `${renderProfileIdentity(profile)}${renderProfileInput(profile, base)}${renderProfileAudio(profile, base)}` : ""}
          ${activeProfileTab === "processing" ? renderProfileRecognition(profile, base) : ""}
          ${activeProfileTab === "output" ? renderProfileOutput(profile, base) : ""}
        </div>
      </div>
    </div>
  </div>`;
}

function renderProfileIdentity(profile: Profile): string {
  return `<section class="settings-section">
    <div><h2 class="section-title">${t("Application")}</h2><div class="section-meta">${t(profile.builtIn ? "Built-in profile" : "Custom profile")}</div></div>
    <div class="field-stack">
      <div class="field"><label>${t("Profile name")}</label><input class="input" data-profile-name-input="true" value="${escapeHtml(profile.id)}" /></div>
      <div class="field">
        <label>${t("Process names")}</label>
        <div class="input-with-button">
          <input class="input" readonly value="${escapeHtml(profileSummary(profile))}" />
          <button class="icon-button" id="open-process-picker" title="${t("Choose processes")}">${icon("fileSearchOutline")}</button>
        </div>
        <div class="field-help">${t("Leave empty to use the application that is in the foreground when PTT is pressed.")}</div>
      </div>
    </div>
  </section>`;
}

function segment(value: string, current: string, label: string, disabled = false): string {
  return `<button class="segment ${value === current ? "active" : ""}" data-segment-value="${value}" ${disabled ? "disabled" : ""}>${label}</button>`;
}

function renderProfileInput(profile: Profile, base: string): string {
  const keyboardKeys = profile.input.keyboard.virtualKeys?.length
    ? profile.input.keyboard.virtualKeys
    : [profile.input.keyboard.virtualKey ?? 119];
  const keyboardPath = `${base}.input.keyboard.virtualKeys`;
  const inputCaptureActive = Boolean(keyboardCapturePath || mouseCaptureProfileId || gamepadCaptureProfileId);
  const steamVr = snapshot!.steamVr;
  const steamVrMessage = steamVr.runtimeInstalled
    ? steamVr.message
    : t("SteamVR is not installed on this computer.");
  return `<section class="settings-section">
    <div><h2 class="section-title">${t("Push to talk")}</h2><div class="section-meta">${t("Trigger binding")}</div></div>
    <div class="field-stack">
      <div class="segmented" data-segment-path="${base}.input.mode">
        ${segment("keyboard", profile.input.mode, `${icon("keyboard")}<span>${t("Keyboard")}</span>`)}
        ${segment("mouse", profile.input.mode, `${icon("mouse")}<span>${t("Mouse")}</span>`)}
        ${segment("xinput", profile.input.mode, `${icon("gamepadVariant")}<span>${t("Gamepad")}</span>`)}
        ${segment("steamvr", profile.input.mode, `${icon("steam")}<span>SteamVR</span>`)}
      </div>
      ${profile.input.mode === "keyboard" ? `<div class="field-row">
        <div class="field"><label>${t("Trigger keys")}</label><div class="input-with-button">
          <input class="input" readonly value="${escapeHtml(keyboardCapturePath === keyboardPath ? t("Waiting for keys...") : formatKeys(keyboardKeys))}" />
          <button class="icon-button" data-capture-path="${keyboardPath}" title="${t("Capture keyboard chord")}" ${inputCaptureActive ? "disabled" : ""}>${icon("keyboard")}</button>
        </div></div>
        <div class="field">${toggleControl(`${base}.input.keyboard.suppressKey`, profile.input.keyboard.suppressKey, t("Suppress trigger keys"))}</div>
      </div>` : ""}
      ${profile.input.mode === "mouse" ? `<div class="field-row">
        <div class="field"><label>${t("Trigger button")}</label><div class="input-with-button">
          <input class="input" readonly value="${escapeHtml(mouseCaptureProfileId === profile.id ? t("Waiting for mouse button...") : formatMouseButton(profile.input.mouse.button))}" />
          <button class="icon-button" id="capture-mouse-button" title="${t("Capture mouse button")}" ${inputCaptureActive ? "disabled" : ""}>${icon("mouse")}</button>
        </div></div>
        <div class="field">${toggleControl(`${base}.input.mouse.suppressButton`, profile.input.mouse.suppressButton, t("Suppress trigger button"))}</div>
      </div>` : ""}
      ${profile.input.mode === "xinput" ? `<div class="field-row">
        <div class="field"><label>${t("Controller")}</label><input class="input" readonly value="${escapeHtml(t("XInput controller {number}", { number: profile.input.gamepad.userIndex + 1 }))}" /><div class="field-help">${t("The controller is detected automatically when binding.")}</div></div>
        <div class="field"><label>${t("Trigger button")}</label><div class="input-with-button">
          <input class="input" readonly value="${escapeHtml(gamepadCaptureProfileId === profile.id ? t("Waiting for button...") : formatGamepadButtons(profile.input.gamepad.buttonMask))}" />
          <button class="icon-button" id="capture-gamepad-button" title="${t("Capture gamepad button")}" ${inputCaptureActive ? "disabled" : ""}>${icon("gamepadVariant")}</button>
        </div></div>
      </div>` : ""}
      ${profile.input.mode === "steamvr" ? `<div class="field-stack">
        <div class="field-row">
          <div class="field"><label>${t("Action path")}</label><input class="input" readonly value="${escapeHtml(profile.input.steamVr.actionPath)}" /></div>
          <div class="field"><label>${t("Poll interval (ms)")}</label><input class="input" type="number" min="1" max="1000" data-number="true" data-config-path="${base}.input.steamVr.pollIntervalMs" value="${profile.input.steamVr.pollIntervalMs}" /></div>
        </div>
        <div class="steamvr-actions">
          <button class="button" id="open-steamvr-bindings">${icon("steam")}<span>${t("Controller bindings")}</span></button>
          <button class="icon-button" id="refresh-steamvr-status" title="${t("Refresh SteamVR status")}">${icon("refresh")}</button>
        </div>
        <div class="steamvr-status ${steamVr.connected ? "connected" : "disconnected"}">
          <span class="steamvr-status-dot"></span>
          <span>${escapeHtml(steamVrMessage)}</span>
        </div>
      </div>` : ""}
    </div>
  </section>`;
}

function renderProfileAudio(profile: Profile, base: string): string {
  const selectedDeviceId = profile.audio.deviceId;
  const globalDevice = configuration!.audio.deviceId
    ? snapshot!.microphones.find(device => device.id === configuration!.audio.deviceId)?.name
      ?? t("Unavailable device")
    : t("Default communications device");
  const selectedDeviceExists = !selectedDeviceId || snapshot!.microphones.some(device => device.id === selectedDeviceId);
  const unavailableOption = selectedDeviceId && !selectedDeviceExists
    ? `<option value="${escapeHtml(selectedDeviceId)}" selected disabled>${escapeHtml(t("Unavailable: {value}", { value: selectedDeviceId }))}</option>`
    : "";
  const microphoneOptions = snapshot!.microphones.map(device =>
    `<option value="${escapeHtml(device.id)}" ${device.id === selectedDeviceId ? "selected" : ""}>${escapeHtml(device.name)}</option>`).join("");

  return `<section class="settings-section">
    <div><h2 class="section-title">${t("Microphone")}</h2><div class="section-meta">${t("Profile audio source")}</div></div>
    <div class="field-stack">
      <div class="field-row">
        <div class="field"><label>${t("Capture device")}</label><select class="select" data-config-path="${base}.audio.deviceId" data-nullable="true">
          <option value="" ${selectedDeviceId ? "" : "selected"}>${escapeHtml(t("Use global: {value}", { value: globalDevice }))}</option>
          ${unavailableOption}${microphoneOptions}
        </select></div>
        <div class="field"><label>${t("Minimum recording (ms)")}</label><input class="input" type="number" min="100" max="5000" placeholder="${t("Global: {value}", { value: configuration!.audio.minimumDurationMs })}" data-config-path="${base}.audio.minimumDurationMs" data-number="true" data-nullable="true" value="${profile.audio.minimumDurationMs ?? ""}" /></div>
      </div>
      ${selectedDeviceId && !selectedDeviceExists ? `<div class="notice">${icon("alertCircleOutline")}<span>${t("The selected microphone is not currently connected.")}</span></div>` : ""}
    </div>
  </section>`;
}

function renderProfileRecognition(profile: Profile, base: string): string {
  const currentStatus = getProviderStatus(profile.recognition.provider);
  const streamsToOsc = profile.output.mode === "vrchat-osc" && profile.output.vrChat.sendImmediately;
  const providerOptions = snapshot!.providerStatuses.map(status =>
    `<option value="${escapeHtml(status.id)}" ${status.id === profile.recognition.provider ? "selected" : ""} ${status.available ? "" : "disabled"}>${escapeHtml(status.id)}${status.available ? "" : ` (${t("not installed")})`}</option>`).join("");
  return `<section class="settings-section">
    <div><h2 class="section-title">${t("Recognition")}</h2><div class="section-meta">${t("ASR policy")}</div></div>
    <div class="field-stack">
      <div class="field-row">
        <div class="field"><label>${t("Provider")}</label><select class="select" data-config-path="${base}.recognition.provider">${providerOptions}</select></div>
        <div class="field"><label>${t("Language")}</label><select class="select" data-config-path="${base}.recognition.language">
          ${[["auto", t("Auto detect")], ["zh", t("Chinese")], ["en", t("English")], ["ja", t("Japanese")], ["ko", t("Korean")]].map(([value, label]) => `<option value="${value}" ${profile.recognition.language === value ? "selected" : ""}>${label}</option>`).join("")}
        </select></div>
      </div>
      ${currentStatus.available ? "" : `<div class="notice">${icon("alertCircleOutline")}<span>${escapeHtml(t("Required files missing: {files}", { files: currentStatus.missingFiles.join(", ") }))}</span></div>`}
      ${toggleControl(
        `${base}.recognition.streamingEnabled`,
        profile.recognition.streamingEnabled,
        t("Stream recognition while recording"),
        !currentStatus.supportsStreaming)}
      ${profile.recognition.streamingEnabled && !currentStatus.streamingAvailable
        ? `<div class="notice">${icon("alertCircleOutline")}<span>${escapeHtml(t("Streaming files missing: {files}", { files: currentStatus.streamingMissingFiles.join(", ") }))}</span></div>`
        : ""}
      ${profile.recognition.streamingEnabled
        ? `<div class="notice">${icon("waveform")}<span>${t(streamsToOsc
          ? "Each completed speech segment updates the VRChat Chatbox; the final update enables its notification sound."
          : "Speech is recognized in segments while recording, then joined and sent once when PTT is released.")}</span></div>`
        : ""}
      ${profile.recognition.streamingEnabled &&
        !["qwen3-asr", "sensevoice-gguf", "paraformer-gguf"].includes(profile.recognition.provider)
        ? `<div class="notice">${icon("alertCircleOutline")}<span>${t("This provider starts an external process for every speech segment and may have higher latency.")}</span></div>`
        : ""}
      <div class="field"><label>${t("Hotwords")}</label><textarea class="textarea" data-array-path="${base}.recognition.hotwords" ${currentStatus.supportsTerminologyHints ? "" : "disabled"}>${escapeHtml(profile.recognition.hotwords.join("\n"))}</textarea></div>
      ${currentStatus.supportsTerminologyHints ? "" : `<div class="notice">${icon("alertCircleOutline")}<span>${t("Hotwords are unavailable for {provider}. Existing entries are retained but inactive.", { provider: profile.recognition.provider })}</span></div>`}
    </div>
  </section>`;
}

function renderProfileOutput(profile: Profile, base: string): string {
  const output = profile.output;
  const openInput = output.windows?.openInput ?? { mode: "none", virtualKeys: [] };
  const submission = output.windows?.submission ?? { mode: "none", virtualKeys: [] };
  const openInputPath = `${base}.output.windows.openInput.virtualKeys`;
  const submissionPath = `${base}.output.windows.submission.virtualKeys`;
  return `<section class="settings-section">
    <div><h2 class="section-title">${t("Output")}</h2><div class="section-meta">${t("Text route and submission")}</div></div>
    <div class="field-stack">
      <div class="segmented" data-segment-path="${base}.output.mode">
        ${segment("captured-window", output.mode, t("Window"))}
        ${segment("vrchat-osc", output.mode, "VRChat OSC")}
      </div>
      ${output.mode === "captured-window" ? `<div class="field-stack">
        <div class="output-stage">
          <div class="field"><label>${t("Open input")}</label><div class="segmented" data-segment-path="${base}.output.windows.openInput.mode">
            ${segment("none", openInput.mode, t("None"))}${segment("hotkey", openInput.mode, t("Hotkey"))}
          </div></div>
          ${openInput.mode === "hotkey" ? `<div class="field-row">
            <div class="field"><label>${t("Open keys")}</label><div class="input-with-button">
              <input class="input" readonly value="${escapeHtml(keyboardCapturePath === openInputPath ? t("Waiting for keys...") : formatKeys(openInput.virtualKeys))}" />
              <button class="icon-button" data-capture-path="${openInputPath}" title="${t("Capture open-input chord")}" ${keyboardCapturePath ? "disabled" : ""}>${icon("keyboard")}</button>
            </div></div>
            <div class="field"><label>${t("Open delay (ms)")}</label><input class="input" type="number" min="0" max="5000" data-number="true" data-config-path="${base}.output.windows.openInputDelayMs" value="${output.windows.openInputDelayMs}" /></div>
          </div>` : ""}
        </div>
        <div class="output-stage"><div class="field-row">
          <div class="field"><label>${t("Text input")}</label><select class="select" data-config-path="${base}.output.windows.textInputMethod">
            <option value="clipboard-paste" ${output.windows.textInputMethod === "clipboard-paste" ? "selected" : ""}>${t("Clipboard paste (default)")}</option>
            <option value="unicode-send-input" ${output.windows.textInputMethod === "unicode-send-input" ? "selected" : ""}>${t("Unicode SendInput")}</option>
            <option value="keyboard" ${output.windows.textInputMethod === "keyboard" ? "selected" : ""}>${t("Keyboard events")}</option>
          </select></div>
          <div class="field">${toggleControl(`${base}.output.windows.requireSameForeground`, output.windows.requireSameForeground, t("Require same foreground window"))}</div>
        </div></div>
        <div class="output-stage">
          <div class="field"><label>${t("Submit")}</label><div class="segmented" data-segment-path="${base}.output.windows.submission.mode">
            ${segment("none", submission.mode, t("None"))}${segment("hotkey", submission.mode, t("Hotkey"))}
          </div></div>
          ${submission.mode === "hotkey" ? `<div class="field"><label>${t("Submit keys")}</label><div class="input-with-button">
            <input class="input" readonly value="${escapeHtml(keyboardCapturePath === submissionPath ? t("Waiting for keys...") : formatKeys(submission.virtualKeys))}" />
            <button class="icon-button" data-capture-path="${submissionPath}" title="${t("Capture submission chord")}" ${keyboardCapturePath ? "disabled" : ""}>${icon("keyboard")}</button>
          </div></div>` : ""}
        </div>
      </div>` : `<div class="field-stack">
        <div class="field-row">
          <div class="field"><label>${t("Host")}</label><input class="input" data-config-path="${base}.output.vrChat.host" value="${escapeHtml(output.vrChat.host)}" /></div>
          <div class="field"><label>${t("Port")}</label><input class="input" type="number" min="1" max="65535" data-number="true" data-config-path="${base}.output.vrChat.port" value="${output.vrChat.port}" /></div>
        </div>
        <div class="field-row">
          <div class="field"><label>${t("Character limit")}</label><input class="input" type="number" min="1" max="144" data-number="true" data-config-path="${base}.output.vrChat.maxChatboxCharacters" value="${output.vrChat.maxChatboxCharacters}" /></div>
          <div class="field">${toggleControl(`${base}.output.vrChat.sendImmediately`, output.vrChat.sendImmediately, t("Send immediately"))}</div>
        </div>
      </div>`}
    </div>
  </section>`;
}

function renderModels(): string {
  const labels: Record<string, string> = {
    paraformer: "Paraformer",
    senseVoice: "SenseVoice",
    funAsrNano: "Fun-ASR-Nano",
    qwen3Asr: "Qwen3-ASR",
    whisperCpp: "Whisper.cpp"
  };
  const providerId = modelProviderIds[selectedModel];
  const status = getProviderStatus(providerId);
  const modelAvailable = isAnyModelCombinationAvailable(selectedModel);
  return `<div class="view">
    <div class="model-tabs">${Object.entries(labels).map(([id, label]) =>
      `<button class="model-tab ${selectedModel === id ? "active" : ""} ${isAnyModelCombinationAvailable(id) ? "available" : "missing"}" data-model-tab="${id}"><span class="model-status-dot"></span>${label}</button>`).join("")}</div>
    ${renderModelDownloadProgress(providerId)}
    <div class="availability-bar ${modelAvailable ? "available" : "missing"}">
      ${icon(modelAvailable ? "check" : "alertCircleOutline")}
      <span>${modelAvailable ? `${escapeHtml(status.id)} ${t("installed and available")}.` : escapeHtml(t("Missing files: {files}", { files: status.missingFiles.join(", ") }))}</span>
    </div>
    ${renderModelVariantSelection(selectedModel)}
    ${renderModelFacts(selectedModel)}
    ${renderModelCapabilities(selectedModel, providerId)}
    ${renderAdvancedModelConfiguration(selectedModel)}
  </div>`;
}

function isAnyModelCombinationAvailable(model: string): boolean {
  const providerId = modelProviderIds[model];
  if (getProviderStatus(providerId).available) return true;

  const assets = snapshot!.modelAssets.filter(asset => asset.providerId === providerId);
  const hasInstalled = (componentId: string): boolean =>
    assets.some(asset => asset.componentId === componentId && asset.installed);
  const hasRuntime = model === "qwen3Asr" || assets.some(asset =>
    asset.componentId.startsWith("runtime-") && asset.installed);
  const hasPrimaryModel = hasInstalled(getPrimaryModelComponentId(model));
  const hasRequiredSecondaryModels = model !== "funAsrNano" || hasInstalled("encoder");
  const hasRequiredPunctuation = model !== "paraformer" ||
    !configuration!.asr.paraformer.usePunctuation ||
    hasInstalled("punctuation");
  return hasRuntime && hasPrimaryModel && hasRequiredSecondaryModels && hasRequiredPunctuation;
}

function renderAdvancedModelConfiguration(model: string): string {
  return `<details class="model-advanced" ${advancedModelConfigurationOpen ? "open" : ""}>
    <summary><strong>${t("Advanced model configuration")}</strong><span class="model-advanced-description">${t("Manual paths, numeric parameters, and streaming thresholds.")}</span><span class="model-advanced-chevron">${icon("chevronDown")}</span></summary>
    <div class="model-advanced-content">
      ${renderModelSettings(model)}
      ${renderStreamingModelSettings()}
    </div>
  </details>`;
}

function getModelAssetStatus(assetId: string): ModelAssetStatus | null {
  return snapshot!.modelAssets.find(asset => asset.id === assetId) ?? null;
}

function getConfiguredModelAssetId(model: string): string {
  const asr = configuration!.asr;
  if (model === "paraformer") {
    const value = getParaformerQuantization(asr.paraformer.modelPath);
    return value === "custom" ? "custom" : paraformerVariants[value].assetId;
  }
  if (model === "senseVoice") {
    const value = getSenseVoiceQuantization(asr.senseVoice.modelPath);
    return value === "custom" ? "custom" : senseVoiceVariants[value].assetId;
  }
  if (model === "funAsrNano") {
    const value = getNanoLanguageModelQuantization(asr.funAsrNano.languageModelPath);
    return value === "custom" ? "custom" : nanoLanguageModelVariants[value].assetId;
  }
  return model === "qwen3Asr" ? "qwen3-asr-int8" : "whisper-small-q5_1";
}

function getSelectedModelAssetId(model: string): string {
  return selectedModelVariants[model] ?? getConfiguredModelAssetId(model);
}

function getModelVariantAssets(model: string): ModelAssetStatus[] {
  const providerId = modelProviderIds[model];
  const componentId = getPrimaryModelComponentId(model);
  return snapshot!.modelAssets.filter(asset => asset.providerId === providerId && asset.componentId === componentId);
}

function getPrimaryModelComponentId(model: string): string {
  if (model === "funAsrNano") return "language-model";
  if (model === "qwen3Asr") return "model-bundle";
  return "model";
}

function modelVariantDetail(model: string, assetId: string): string {
  if (model === "paraformer") {
    const item = Object.values(paraformerVariants).find(value => value.assetId === assetId);
    return item ? `${t(item.description)} · ${item.fileSize} · ${item.peakRam} RAM · ${formatBenchmarkThroughput(item.charactersPerSecond)}` : t("Custom model path");
  }
  if (model === "senseVoice") {
    const item = Object.values(senseVoiceVariants).find(value => value.assetId === assetId);
    if (!item) return t("Custom model path");
    return configuration!.asr.senseVoice.backend === "vulkan"
      ? `${t(item.description)} · ${item.fileSize} · Vulkan · ${t("Device-specific performance not measured")}`
      : `${t(item.description)} · ${item.fileSize} · ${item.peakRam} RAM · ${formatBenchmarkThroughput(item.charactersPerSecond)}`;
  }
  if (model === "funAsrNano") {
    const item = Object.values(nanoLanguageModelVariants).find(value => value.assetId === assetId);
    if (!item) return t("Custom model path");
    return configuration!.asr.funAsrNano.backend === "vulkan"
      ? `${t(item.description)} · ${item.fileSize} · Vulkan · ${t("Device-specific performance not measured")}`
      : `${t(item.description)} · ${item.fileSize}`;
  }
  return model === "qwen3Asr"
    ? `INT8 · ~941 MiB · ~2.2-3.0 GiB RAM · ${formatBenchmarkThroughput(28)}`
    : `Small Q5_1 · ~181 MiB · ~590 MiB RAM · ${formatBenchmarkThroughput(12)}`;
}

function formatBenchmarkThroughput(charactersPerSecond: number | null): string {
  return charactersPerSecond === null
    ? t("Not measured")
    : t("~{rate} characters/s", { rate: charactersPerSecond });
}

function renderModelVariantSelection(model: string): string {
  const assets = getModelVariantAssets(model);
  const configuredId = getConfiguredModelAssetId(model);
  const selectedId = getSelectedModelAssetId(model);
  const selectedAsset = getModelAssetStatus(selectedId);
  const anyDownloadActive = modelDownload !== null && ["checking", "downloading", "verifying", "extracting"].includes(modelDownload.state);
  const options = assets.map(asset => `<option value="${escapeHtml(asset.id)}" ${asset.id === selectedId ? "selected" : ""}>${escapeHtml(asset.variant)} · ${formatBytes(asset.size)}${asset.installed ? "" : ` · ${t("Not installed")}`}</option>`).join("");
  const customOption = configuredId === "custom" ? `<option value="custom" ${selectedId === "custom" ? "selected" : ""}>${t("Custom model")}</option>` : "";
  const selectionLabel = model === "funAsrNano" ? t("Language model quantization") : t("Quantization");
  return `<section class="model-selection">
    <div class="model-selection-heading"><h2>${selectionLabel}</h2><span>${t("Select an installed version to use it, or download a missing version first.")}</span></div>
    <div class="model-selection-control">
      <select class="select" id="model-variant-select" ${assets.length <= 1 && configuredId !== "custom" ? "disabled" : ""}>${options}${customOption}</select>
      <div class="model-selection-detail">${modelVariantDetail(model, selectedId)}</div>
      ${selectedId === "custom"
        ? `<span class="model-asset-status installed">${t("Custom path")}</span>`
        : selectedAsset?.installed
          ? `<span class="model-asset-status installed">${icon("check")} ${t("Installed and active")}</span>`
          : `<button class="button" data-download-model-variant="${escapeHtml(selectedId)}" ${anyDownloadActive ? "disabled" : ""}>${icon("download")}<span>${t("Download and use")}</span></button>`}
    </div>
  </section>`;
}

function capabilitySelectionKey(providerId: string, componentId: string): string {
  return `${providerId}:${componentId}`;
}

function normalizeModelPath(path: string | null | undefined): string {
  return (path ?? "").trim().replaceAll("\\", "/").toLowerCase();
}

function pathsReferToSameFile(left: string | null | undefined, right: string | null | undefined): boolean {
  const normalizedLeft = normalizeModelPath(left);
  const normalizedRight = normalizeModelPath(right);
  if (!normalizedLeft || !normalizedRight) return false;
  return normalizedLeft === normalizedRight
    || normalizedLeft.split("/").at(-1) === normalizedRight.split("/").at(-1);
}

function getConfiguredCapabilityPath(providerId: string, componentId: string): string | null {
  const asr = configuration!.asr;
  if (componentId === "encoder" && providerId === "funasr-nano-gguf") return asr.funAsrNano.encoderModelPath;
  if (componentId === "language-model" && providerId === "funasr-nano-gguf") return asr.funAsrNano.languageModelPath;
  if (componentId === "punctuation") return asr.paraformer.punctuationModelPath;
  if (componentId === "streaming-vad") return asr.streaming.sileroVadModelPath;
  if (componentId !== "vad") return null;
  if (providerId === "paraformer-gguf") return asr.paraformer.vadModelPath;
  if (providerId === "sensevoice-gguf") return asr.senseVoice.vadModelPath;
  if (providerId === "funasr-nano-gguf") return asr.funAsrNano.vadModelPath;
  if (providerId === "whisper-cpp") return asr.whisperCpp.vadModelPath;
  return null;
}

function isRuntimeComponentActive(providerId: string, componentId: string): boolean {
  if (componentId !== "runtime-cpu" && componentId !== "runtime-vulkan") return false;
  const wantsVulkan = componentId === "runtime-vulkan";
  if (providerId === "sensevoice-gguf") return (configuration!.asr.senseVoice.backend === "vulkan") === wantsVulkan;
  if (providerId === "funasr-nano-gguf") return (configuration!.asr.funAsrNano.backend === "vulkan") === wantsVulkan;
  if (providerId === "whisper-cpp") return configuration!.asr.whisperCpp.useGpu === wantsVulkan;
  return providerId === "paraformer-gguf" && !wantsVulkan;
}

function getSelectedCapabilityAsset(
  providerId: string,
  componentId: string,
  assets: ModelAssetStatus[]
): ModelAssetStatus | null {
  const selectedId = selectedCapabilityVariants[capabilitySelectionKey(providerId, componentId)];
  if (selectedId) return assets.find(asset => asset.id === selectedId) ?? null;
  const configuredPath = getConfiguredCapabilityPath(providerId, componentId);
  const configuredAsset = assets.find(asset => pathsReferToSameFile(configuredPath, asset.targetPath));
  if (configuredAsset) return configuredAsset;
  return configuredPath && assets.length > 1 ? null : assets[0] ?? null;
}

function renderModelCapabilities(model: string, providerId: string): string {
  const assets = snapshot!.modelAssets.filter(asset =>
    asset.providerId === providerId && asset.componentId !== getPrimaryModelComponentId(model));
  const componentLabels: Record<string, string> = {
    "runtime-cpu": t("CPU runtime"),
    "runtime-vulkan": t("Vulkan runtime"),
    encoder: t("Encoder model"),
    "language-model": t("Language model"),
    punctuation: t("Punctuation model"),
    vad: t("VAD model"),
    "streaming-vad": t("Streaming VAD model")
  };
  const descriptions: Record<string, string> = {
    "runtime-cpu": t("Native executable and libraries required for CPU inference."),
    "runtime-vulkan": t("Native executable and libraries required for Vulkan inference."),
    encoder: t("Audio encoder used before language-model decoding."),
    "language-model": t("Required language model for decoding."),
    punctuation: t("Restores Chinese and English punctuation."),
    vad: t("Optional backend speech segmentation."),
    "streaming-vad": t("Enables segmented streaming; turn it on per preset.")
  };
  const anyDownloadActive = modelDownload !== null && ["checking", "downloading", "verifying", "extracting"].includes(modelDownload.state);
  const componentGroups = new Map<string, ModelAssetStatus[]>();
  for (const asset of assets) {
    const group = componentGroups.get(asset.componentId) ?? [];
    group.push(asset);
    componentGroups.set(asset.componentId, group);
  }
  const rows = [...componentGroups.entries()].map(([componentId, componentAssets]) => {
    const selectedAsset = getSelectedCapabilityAsset(providerId, componentId, componentAssets);
    const configuredPath = getConfiguredCapabilityPath(providerId, componentId);
    const selectionKey = capabilitySelectionKey(providerId, componentId);
    const isCustom = componentAssets.length > 1 && selectedAsset === null;
    const isRuntime = componentId === "runtime-cpu" || componentId === "runtime-vulkan";
    const isActive = selectedAsset !== null && (isRuntime
      ? isRuntimeComponentActive(providerId, componentId)
      : pathsReferToSameFile(configuredPath, selectedAsset.targetPath));
    const punctuationMissing = componentId === "punctuation"
      && configuration!.asr.paraformer.usePunctuation
      && !selectedAsset?.installed;
    const description = punctuationMissing
      ? `<span class="model-capability-warning">${icon("alertCircleOutline")}<span>${t("Punctuation is enabled but its model is missing. Install this capability to make Paraformer available.")}</span></span>`
      : `<span>${descriptions[componentId] ?? ""}</span>`;
    const status = isCustom
      ? t("Custom path")
      : punctuationMissing
        ? t("Enabled but missing")
        : selectedAsset?.updateAvailable
          ? t("Update available")
        : selectedAsset?.installed
          ? t(isActive ? "Installed and active" : "Available")
          : t("Not installed");
    const variantControl = componentAssets.length > 1
      ? `<select class="select model-capability-variant-select" data-capability-variant-key="${escapeHtml(selectionKey)}">
          ${componentAssets.map(asset => `<option value="${escapeHtml(asset.id)}" ${asset.id === selectedAsset?.id ? "selected" : ""}>${escapeHtml(asset.variant)} · ${t(asset.installed ? "Installed" : "Not installed")}</option>`).join("")}
          ${isCustom ? `<option value="custom" selected>${t("Custom path")}</option>` : ""}
        </select>`
      : `<span class="model-capability-variant">· ${escapeHtml(componentAssets[0]?.variant ?? "")}</span>`;
    const action = isCustom
      ? `<span class="capability-action-placeholder" aria-hidden="true"></span>`
      : selectedAsset?.updateAvailable
        ? `<button class="icon-button" data-download-asset="${escapeHtml(selectedAsset.id)}" title="${t("Update runtime")}" ${anyDownloadActive ? "disabled" : ""}>${icon("download")}</button>`
      : selectedAsset?.installed
        ? `<span class="capability-ready">${icon("check")}</span>`
        : `<button class="icon-button" data-download-asset="${escapeHtml(selectedAsset?.id ?? "")}" title="${t("Install capability")}" ${anyDownloadActive ? "disabled" : ""}>${icon("download")}</button>`;

    return `<div class="model-capability-row">
      <div class="model-capability-name"><div class="model-capability-title"><strong>${componentLabels[componentId] ?? escapeHtml(componentId)}</strong>${variantControl}</div>${description}</div>
      ${componentId === "punctuation"
        ? `<label class="capability-toggle"><input type="checkbox" data-config-path="asr.paraformer.usePunctuation" ${configuration!.asr.paraformer.usePunctuation ? "checked" : ""} ${selectedAsset?.installed ? "" : "disabled"} /><span>${t("Enable")}</span></label>`
        : `<span class="capability-toggle-placeholder" aria-hidden="true"></span>`}
      <span class="model-asset-size">${selectedAsset ? formatBytes(selectedAsset.size) : "-"}</span>
      <span class="model-asset-status ${selectedAsset?.installed || isCustom ? "installed" : "missing"}">${status}</span>
      ${action}
    </div>`;
  }).join("");
  const senseVoiceOptions = model === "senseVoice" ? `<div class="model-capability-options">
    ${toggleControl("asr.senseVoice.backend", configuration!.asr.senseVoice.backend === "vulkan", t("Use GPU"), false, "vulkan", "cpu")}
    ${gpuDeviceSelect(configuration!.asr.senseVoice.vulkanDeviceIndex, configuration!.asr.senseVoice.backend !== "vulkan", "sensevoice")}
    <div class="notice">${icon("alertCircleOutline")}<span>${t("Vulkan performance may be faster or slower depending on the device, but an integrated GPU can be used to reduce CPU load.")}</span></div>
  </div>` : "";
  const nanoOptions = model === "funAsrNano" ? `<div class="model-capability-options">
    ${toggleControl("asr.funAsrNano.backend", configuration!.asr.funAsrNano.backend === "vulkan", t("Use GPU"), false, "vulkan", "cpu")}
    ${gpuDeviceSelect(configuration!.asr.funAsrNano.vulkanDeviceIndex, configuration!.asr.funAsrNano.backend !== "vulkan", "funasr-nano")}
    <div class="notice">${icon("alertCircleOutline")}<span>${t("Nano Vulkan can reduce CPU load but may be slower and reserve about 2 GiB of GPU or shared memory.")}</span></div>
  </div>` : "";
  const whisperOptions = model === "whisperCpp" ? `<div class="model-capability-options">
    ${toggleControl("asr.whisperCpp.useGpu", configuration!.asr.whisperCpp.useGpu, t("Use GPU"))}
    ${gpuDeviceSelect(configuration!.asr.whisperCpp.gpuDeviceIndex, !configuration!.asr.whisperCpp.useGpu, "whisper")}
  </div>` : "";
  return `<section class="model-capabilities">
    <div class="model-capabilities-heading"><h2>${t("Model components and optional capabilities")}</h2><span>${t("Install only the components and capabilities needed by this model.")}</span></div>
    <div class="model-capability-list">${rows}${senseVoiceOptions}${nanoOptions}${whisperOptions}</div>
  </section>`;
}

function renderModelFacts(model: string): string {
  const selectedAssetId = getSelectedModelAssetId(model);
  const paraformerVariant = Object.values(paraformerVariants).find(item => item.assetId === selectedAssetId) ?? null;
  const senseVoiceVariant = Object.values(senseVoiceVariants).find(item => item.assetId === selectedAssetId) ?? null;
  const senseVoiceUsesVulkan = configuration!.asr.senseVoice.backend === "vulkan";
  const nanoUsesVulkan = configuration!.asr.funAsrNano.backend === "vulkan";
  const nanoLanguageAsset = model === "funAsrNano" ? getModelAssetStatus(selectedAssetId) : null;
  const nanoEncoderAssets = snapshot!.modelAssets.filter(asset =>
    asset.providerId === "funasr-nano-gguf" && asset.componentId === "encoder");
  const nanoEncoderAsset = getSelectedCapabilityAsset("funasr-nano-gguf", "encoder", nanoEncoderAssets);
  const nanoBenchmark = nanoEncoderAsset && nanoLanguageAsset
    ? nanoCombinationBenchmarks[`${nanoEncoderAsset.id}:${nanoLanguageAsset.id}`] ?? null
    : null;
  const facts: Record<string, { languages: string; files: string; ram: string; throughput: string; bestFor: string }> = {
    paraformer: {
      languages: t("Mandarin Chinese; limited English code-switching"),
      files: paraformerVariant
        ? `${paraformerVariant.fileSize} (+1.6 MiB VAD; +72.0 MiB ${t("optional punctuation")})`
        : t("Custom model"),
      ram: paraformerVariant ? paraformerVariant.peakRam : t("Not measured"),
      throughput: paraformerVariant ? formatBenchmarkThroughput(paraformerVariant.charactersPerSecond) : t("Not measured"),
      bestFor: t("Fast Mandarin input with the lowest CPU and memory cost.")
    },
    senseVoice: {
      languages: t("Mandarin, Cantonese, English, Japanese, Korean"),
      files: senseVoiceVariant ? `${senseVoiceVariant.fileSize} (+1.6 MiB VAD)` : t("Custom model"),
      ram: senseVoiceVariant && !senseVoiceUsesVulkan ? senseVoiceVariant.peakRam : t("Not measured"),
      throughput: senseVoiceVariant && !senseVoiceUsesVulkan ? formatBenchmarkThroughput(senseVoiceVariant.charactersPerSecond) : t("Not measured"),
      bestFor: t("Low-latency multilingual input, especially East Asian languages.")
    },
    funAsrNano: {
      languages: t("Chinese, English, Japanese"),
      files: nanoEncoderAsset && nanoLanguageAsset
        ? formatBytes(nanoEncoderAsset.size + nanoLanguageAsset.size)
        : t("Custom model"),
      ram: !nanoUsesVulkan && nanoBenchmark ? nanoBenchmark.peakRam : t("Not measured"),
      throughput: !nanoUsesVulkan && nanoBenchmark ? formatBenchmarkThroughput(nanoBenchmark.charactersPerSecond) : t("Not measured"),
      bestFor: t("Higher-quality terminology and punctuation when more RAM is available.")
    },
    qwen3Asr: {
      languages: t("30 languages and 22 Chinese dialects"),
      files: "~941 MiB",
      ram: "~2.2-3.0 GiB",
      throughput: formatBenchmarkThroughput(28),
      bestFor: t("Higher-quality multilingual and mixed-language PTT with effective terminology hints.")
    },
    whisperCpp: {
      languages: t("Multilingual (Whisper Small, non-English-only model)"),
      files: "~181 MiB",
      ram: "~590 MiB",
      throughput: formatBenchmarkThroughput(12),
      bestFor: t("Broad language coverage when higher latency is acceptable.")
    }
  };
  const fact = facts[model];
  const throughputToolTip = escapeHtml(t("Measured on an Intel Core i7-14700KF system with 64 GB RAM."));
  return `<div class="model-facts">
    <div class="model-fact"><span>${t("Languages")}</span><strong>${fact.languages}</strong></div>
    <div class="model-fact"><span>${t("Model files")}</span><strong>${fact.files}</strong></div>
    <div class="model-fact"><span>${t("Estimated peak RAM")}</span><strong>${fact.ram}</strong></div>
    <div class="model-fact" title="${throughputToolTip}"><span class="model-fact-label">${t("Tested throughput")}${icon("informationOutline")}</span><strong>${fact.throughput}</strong></div>
    <div class="model-fact wide"><span>${t("Best suited for")}</span><strong>${fact.bestFor}</strong></div>
    <div class="model-facts-note">${t("Memory figures are previous local peak measurements. Throughput is the mean of three resident CPU runs on an 8-second mixed-language clip using an i7-14700KF; whitespace is excluded and actual results vary by hardware, audio, and text.")}</div>
  </div>`;
}

function renderModelDownloadProgress(providerId: string): string {
  const active = modelDownload !== null && ["checking", "downloading", "verifying", "extracting"].includes(modelDownload.state);
  const progress = modelDownload?.providerId === providerId || active ? modelDownload : null;
  if (!progress || !active) return "";
  const percentage = progress?.totalBytes
    ? Math.min(100, Math.round(progress.bytesDownloaded * 100 / progress.totalBytes))
    : 0;
  const detail = progress
    ? `${progress.providerId === providerId ? "" : `${escapeHtml(progress.providerId)}: `}${localizedDownloadMessage(progress)} ${formatBytes(progress.bytesDownloaded)} / ${formatBytes(progress.totalBytes)}`
    : t("Official pinned files are downloaded from release sources and verified with SHA-256.");
  return `<div class="model-download">
    <div class="model-download-actions">
      <button class="icon-button" id="cancel-model-download" title="${t("Cancel download")}">${icon("close")}</button>
      <span class="model-download-detail">${detail}</span>
    </div>
    ${progress ? `<div class="download-progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${percentage}"><span style="width:${percentage}%"></span></div>` : ""}
  </div>`;
}

function localizedDownloadMessage(progress: ModelDownloadProgress): string {
  if (progress.state === "checking") {
    return progress.fileName ? t("Verified {file}.", { file: progress.fileName }) : t("Checking installed model files.");
  }
  if (progress.state === "downloading") return t("Downloading {file}.", { file: progress.fileName ?? "" });
  if (progress.state === "verifying") return t("Verifying {file}.", { file: progress.fileName ?? "" });
  if (progress.state === "extracting") return t("Extracting {file}.", { file: progress.fileName ?? "" });
  if (progress.state === "completed") return t("Model files are installed and verified.");
  if (progress.state === "canceled") return t("Download canceled. Partial files will be resumed next time.");
  return escapeHtml(progress.message);
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 MB";
  const units = ["B", "KB", "MB", "GB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** index).toFixed(index >= 2 ? 1 : 0)} ${units[index]}`;
}

function pathField(label: string, path: string, value: string | null, kind: "executable" | "model" | "folder"): string {
  return `<div class="field"><label>${label}</label><div class="input-with-button">
    <input class="input" data-config-path="${path}" data-nullable="true" value="${escapeHtml(value ?? "")}" />
    <button class="icon-button" data-browse-path="${path}" data-browse-kind="${kind}" title="${t("Choose file")}">${icon("folderOpenOutline")}</button>
  </div></div>`;
}

function gpuDeviceSelect(selectedIndex: number | null, disabled: boolean, target: "sensevoice" | "funasr-nano" | "whisper"): string {
  const selectedExists = selectedIndex === null || snapshot!.gpuDevices.some(device => device.index === selectedIndex);
  const unavailableOption = selectedIndex !== null && !selectedExists
    ? `<option value="${selectedIndex}" selected>${t("Unavailable GPU device {number}", { number: selectedIndex })}</option>`
    : "";
  const options = snapshot!.gpuDevices.length === 0
    ? `<option value="">${t("No Vulkan GPU detected")}</option>`
    : [
        `<option value="" ${selectedIndex === null ? "selected" : ""}>${t("Automatic GPU selection")}</option>`,
        ...snapshot!.gpuDevices.map(device =>
          `<option value="${device.index}" ${device.index === selectedIndex ? "selected" : ""}>${escapeHtml(device.name)} (${t(device.deviceType)})</option>`)
      ].join("");

  return `<div class="field"><label>${t("GPU device")}</label><div class="input-with-button">
    <select class="select" data-gpu-target="${target}" ${disabled || snapshot!.gpuDevices.length === 0 ? "disabled" : ""}>${unavailableOption}${options}</select>
    <button class="icon-button" data-refresh-gpu title="${t("Refresh GPU devices")}">${icon("refresh")}</button>
  </div></div>`;
}

function renderStreamingModelSettings(): string {
  const streaming = configuration!.asr.streaming;
  return `<section class="settings-section"><div><h2 class="section-title">${t("Streaming recognition")}</h2><div class="section-meta">Silero VAD · sherpa-onnx</div></div><div class="field-stack">
    <div class="field-row">
      <div class="field"><label>${t("Speech threshold")}</label><input class="input" type="number" min="0.01" max="1" step="0.05" data-number="true" data-config-path="asr.streaming.threshold" value="${streaming.threshold}" /></div>
      <div class="field"><label>${t("Minimum silence (seconds)")}</label><input class="input" type="number" min="0.1" max="5" step="0.1" data-number="true" data-config-path="asr.streaming.minimumSilenceSeconds" value="${streaming.minimumSilenceSeconds}" /></div>
    </div>
    <div class="field-row">
      <div class="field"><label>${t("Minimum speech (seconds)")}</label><input class="input" type="number" min="0.05" max="5" step="0.05" data-number="true" data-config-path="asr.streaming.minimumSpeechSeconds" value="${streaming.minimumSpeechSeconds}" /></div>
      <div class="field"><label>${t("Maximum segment (seconds)")}</label><input class="input" type="number" min="1" max="60" step="1" data-number="true" data-config-path="asr.streaming.maximumSegmentSeconds" value="${streaming.maximumSegmentSeconds}" /></div>
    </div>
    ${pathField(t("Silero VAD model"), "asr.streaming.sileroVadModelPath", streaming.sileroVadModelPath, "model")}
    <div class="notice">${icon("alertCircleOutline")}<span>${t("Streaming recognition is enabled separately for each preset. OSC can show completed segments; other outputs receive only joined final text.")}</span></div>
  </div></section>`;
}

function renderModelSettings(model: string): string {
  const asr = configuration!.asr;
  if (model === "paraformer") {
    return `<section class="settings-section"><div><h2 class="section-title">Paraformer</h2><div class="section-meta">${t("Manual runtime and model paths")}</div></div><div class="field-stack">
      ${pathField(t("Executable"), "asr.paraformer.executablePath", asr.paraformer.executablePath, "executable")}
      ${pathField(t("Model"), "asr.paraformer.modelPath", asr.paraformer.modelPath, "model")}
      ${pathField(t("VAD model"), "asr.paraformer.vadModelPath", asr.paraformer.vadModelPath, "model")}
      ${pathField(t("Punctuation model"), "asr.paraformer.punctuationModelPath", asr.paraformer.punctuationModelPath, "model")}
    </div></section>`;
  }
  if (model === "senseVoice") {
    return `<section class="settings-section"><div><h2 class="section-title">SenseVoice</h2><div class="section-meta">${t("Manual runtime and model paths")}</div></div><div class="field-stack">
      ${pathField(t("CPU executable"), "asr.senseVoice.cpuExecutablePath", asr.senseVoice.cpuExecutablePath, "executable")}
      ${pathField(t("Vulkan executable"), "asr.senseVoice.vulkanExecutablePath", asr.senseVoice.vulkanExecutablePath, "executable")}
      ${pathField(t("Model"), "asr.senseVoice.modelPath", asr.senseVoice.modelPath, "model")}
      ${pathField(t("VAD model"), "asr.senseVoice.vadModelPath", asr.senseVoice.vadModelPath, "model")}
    </div></section>`;
  }
  if (model === "funAsrNano") {
    return `<section class="settings-section"><div><h2 class="section-title">Fun-ASR-Nano</h2><div class="section-meta">${t("Manual runtime and model parameters")}</div></div><div class="field-stack">
      <div class="field"><label>${t("Chunk seconds")}</label><input class="input" type="number" min="1" max="60" data-number="true" data-config-path="asr.funAsrNano.chunkSeconds" value="${asr.funAsrNano.chunkSeconds}" /></div>
      ${pathField(t("CPU executable"), "asr.funAsrNano.executablePath", asr.funAsrNano.executablePath, "executable")}
      ${pathField(t("Vulkan executable"), "asr.funAsrNano.vulkanExecutablePath", asr.funAsrNano.vulkanExecutablePath, "executable")}
      ${pathField(t("Encoder model"), "asr.funAsrNano.encoderModelPath", asr.funAsrNano.encoderModelPath, "model")}
      ${pathField(t("Language model"), "asr.funAsrNano.languageModelPath", asr.funAsrNano.languageModelPath, "model")}
      ${pathField(t("VAD model"), "asr.funAsrNano.vadModelPath", asr.funAsrNano.vadModelPath, "model")}
    </div></section>`;
  }
  if (model === "qwen3Asr") {
    return `<section class="settings-section"><div><h2 class="section-title">Qwen3-ASR</h2><div class="section-meta">${t("Manual model parameters and paths")}</div></div><div class="field-stack">
      <div class="field-row">
        <div class="field"><label>${t("Threads")}</label><input class="input" type="number" min="1" max="64" data-number="true" data-config-path="asr.qwen3Asr.threadCount" value="${asr.qwen3Asr.threadCount}" /></div>
        <div class="field"><label>${t("Maximum output tokens")}</label><input class="input" type="number" min="16" max="512" data-number="true" data-config-path="asr.qwen3Asr.maxNewTokens" value="${asr.qwen3Asr.maxNewTokens}" /></div>
      </div>
      ${pathField(t("Convolution frontend"), "asr.qwen3Asr.convFrontendPath", asr.qwen3Asr.convFrontendPath, "model")}
      ${pathField(t("Encoder model"), "asr.qwen3Asr.encoderPath", asr.qwen3Asr.encoderPath, "model")}
      ${pathField(t("Decoder model"), "asr.qwen3Asr.decoderPath", asr.qwen3Asr.decoderPath, "model")}
      ${pathField(t("Tokenizer directory"), "asr.qwen3Asr.tokenizerPath", asr.qwen3Asr.tokenizerPath, "folder")}
    </div></section>`;
  }
  return `<section class="settings-section"><div><h2 class="section-title">Whisper</h2><div class="section-meta">${t("Manual runtime and model parameters")}</div></div><div class="field-stack">
    <div class="field-row">
      <div class="field"><label>${t("Language")}</label><input class="input" data-config-path="asr.whisperCpp.language" value="${escapeHtml(asr.whisperCpp.language)}" /></div>
      <div class="field"><label>${t("Threads")}</label><input class="input" type="number" min="0" max="64" data-number="true" data-config-path="asr.whisperCpp.threadCount" value="${asr.whisperCpp.threadCount}" /></div>
    </div>
    ${pathField(t("CPU server executable"), "asr.whisperCpp.serverExecutablePath", asr.whisperCpp.serverExecutablePath, "executable")}
    ${pathField(t("Vulkan server executable"), "asr.whisperCpp.vulkanServerExecutablePath", asr.whisperCpp.vulkanServerExecutablePath, "executable")}
    ${pathField(t("Model"), "asr.whisperCpp.modelPath", asr.whisperCpp.modelPath, "model")}
    ${pathField(t("VAD model"), "asr.whisperCpp.vadModelPath", asr.whisperCpp.vadModelPath, "model")}
  </div></section>`;
}

function renderDiagnostics(): string {
  const profiles = configuration!.profiles.items;
  if (!profiles.some(profile => profile.id === diagnosticProfileId)) {
    diagnosticProfileId = snapshot!.runtime.profileOverride && profiles.some(profile => profile.id === snapshot!.runtime.profileOverride)
      ? snapshot!.runtime.profileOverride
      : profiles[0]?.id ?? "";
  }
  const selectedProfile = profiles.find(profile => profile.id === diagnosticProfileId) ?? null;
  const profileOptions = profiles.map(profile =>
    `<option value="${escapeHtml(profile.id)}" ${profile.id === diagnosticProfileId ? "selected" : ""}>${escapeHtml(profile.id)} · ${escapeHtml(profile.output.mode)}</option>`).join("");
  const applications = snapshot!.runningApplications;
  const configuredProcessNames = new Set(
    (selectedProfile?.match.processNames ?? []).map(name => name.replace(/\.exe$/i, "").toLowerCase()));
  if (!applications.some(application => application.processId === diagnosticTargetProcessId)) {
    diagnosticTargetProcessId = applications.find(application =>
      configuredProcessNames.has(application.processName.replace(/\.exe$/i, "").toLowerCase()))?.processId ??
      applications[0]?.processId ?? null;
  }
  const applicationOptions = applications.map(application =>
    `<option value="${application.processId}" ${application.processId === diagnosticTargetProcessId ? "selected" : ""}>${escapeHtml(application.displayName)}</option>`).join("");
  const requiresTarget = selectedProfile?.output.mode === "captured-window";
  const averageMilliseconds = snapshot!.diagnostics.averageRecognitionMilliseconds;
  const averageText = averageMilliseconds === null
    ? t("No samples")
    : `${Math.round(averageMilliseconds)} ms · ${t("{count} samples", { count: snapshot!.diagnostics.recognitionCount })}`;
  const logEntries = snapshot!.logs.length === 0
    ? `<div class="empty-state">${t("No log entries")}</div>`
    : snapshot!.logs.slice().reverse().map(entry => {
      const time = new Date(entry.timestamp).toLocaleTimeString([], { hour12: false });
      return `<div class="log-entry ${escapeHtml(entry.code)}"><span class="log-time">${escapeHtml(time)}</span><span class="log-code">${escapeHtml(entry.code)}</span><span>${entry.profileId ? `[${escapeHtml(entry.profileId)}] ` : ""}${escapeHtml(entry.message)}</span></div>`;
    }).join("");
  return `<div class="view">
    <div class="diagnostic-grid">
      <div class="diagnostic-cell"><div class="diagnostic-label">${t("Service status")}</div><div class="diagnostic-value">${t(snapshot!.runtime.isRunning ? "Running" : "Stopped")}</div></div>
      <div class="diagnostic-cell"><div class="diagnostic-label">${t("Microphones")}</div><div class="diagnostic-value">${snapshot!.microphones.length}</div></div>
      <div class="diagnostic-cell"><div class="diagnostic-label">${t("Profiles")}</div><div class="diagnostic-value">${configuration!.profiles.items.length}</div></div>
      <div class="diagnostic-cell"><div class="diagnostic-label">${t("Current memory")}</div><div class="diagnostic-value" id="diagnostic-memory">${formatBytes(snapshot!.diagnostics.workingSetBytes)}</div></div>
      <div class="diagnostic-cell"><div class="diagnostic-label">${t("Average generation time")}</div><div class="diagnostic-value" id="diagnostic-average">${averageText}</div></div>
    </div>
    <section class="settings-section">
      <div><h2 class="section-title">${t("Output test")}</h2><div class="section-meta">${t("Send text through the selected preset output")}</div></div>
      <div class="field-stack">
        <div class="field-row">
          <div class="field"><label>${t("Profile")}</label><select class="select" id="output-test-profile" ${profiles.length === 0 ? "disabled" : ""}>${profileOptions}</select></div>
          <div class="field"><label>${t("Message")}</label><input class="input" id="output-test-message" value="VRChat Voice Input test" /></div>
        </div>
        ${requiresTarget ? `<div class="field"><label>${t("Target application")}</label><select class="select" id="output-test-target" ${applications.length === 0 ? "disabled" : ""}>${applicationOptions || `<option>${t("No running applications found")}</option>`}</select></div>` : ""}
        <div><button class="button" id="output-test" ${profiles.length === 0 || (requiresTarget && applications.length === 0) ? "disabled" : ""}>${icon("send")}<span>${t("Send test")}</span></button></div>
      </div>
    </section>
    <section class="settings-section">
      <div><h2 class="section-title">${t("Local log file")}</h2><div class="section-meta">${t("Errors and runtime events are retained for 14 days")}</div></div>
      <div class="field-stack">
        <div class="field"><label>${t("Log file")}</label><div class="input-with-button">
          <input class="input" readonly value="${escapeHtml(snapshot!.environment.logFilePath)}" />
          <button class="icon-button" id="open-log-location" title="${t("Open log file location")}">${icon("folderOpenOutline")}</button>
        </div></div>
      </div>
    </section>
    <div class="log-toolbar"><h2>${t("Runtime log")}</h2><button class="icon-button" id="refresh-snapshot" title="${t("Refresh")}">${icon("refresh")}</button><button class="icon-button" id="clear-logs" title="${t("Clear view")}">${icon("deleteOutline")}</button></div>
    <div class="log-view" id="log-view">${logEntries}</div>
  </div>`;
}

function bindCommonEvents(): void {
  document.querySelectorAll<HTMLButtonElement>("[data-view]").forEach(button => {
    button.addEventListener("click", () => {
      activeView = button.dataset.view as ViewId;
      render();
    });
  });

  document.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>("[data-config-path]").forEach(control => {
    const eventName = control instanceof HTMLInputElement && control.type === "text" ? "input" : "change";
    control.addEventListener(eventName, () => {
      let value: unknown;
      if (control instanceof HTMLInputElement && control.type === "checkbox") {
        value = control.dataset.checkedValue === undefined
          ? control.checked
          : control.checked
            ? control.dataset.checkedValue
            : control.dataset.uncheckedValue;
      } else if (control.dataset.nullable === "true" && control.value.trim() === "") {
        value = null;
      } else if (control.dataset.number === "true") {
        value = Number(control.value);
      } else {
        value = control.value;
      }
      setPath(control.dataset.configPath!, value);
      if (control.dataset.uiLanguage === "true") {
        applyUiLanguage(String(value));
        render();
      } else if (["asr.senseVoice.backend", "asr.funAsrNano.backend", "asr.whisperCpp.useGpu"].includes(control.dataset.configPath!)) {
        render();
      }
    });
  });

  document.querySelectorAll<HTMLTextAreaElement>("[data-array-path]").forEach(control => {
    control.addEventListener("input", () => {
      const values = control.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
      setPath(control.dataset.arrayPath!, [...new Set(values)]);
    });
  });

  document.querySelectorAll<HTMLElement>("[data-segment-path]").forEach(group => {
    group.querySelectorAll<HTMLButtonElement>("[data-segment-value]").forEach(button => {
      button.addEventListener("click", () => {
        setPath(group.dataset.segmentPath!, button.dataset.segmentValue!);
        render();
      });
    });
  });

  document.querySelectorAll<HTMLButtonElement>("[data-capture-path]").forEach(button => {
    button.addEventListener("click", () => {
      void captureKeyboardChord(button.dataset.capturePath!);
    });
  });

  document.querySelectorAll<HTMLButtonElement>("[data-browse-path]").forEach(button => {
    button.addEventListener("click", async () => {
      const path = button.dataset.browsePath!;
      const currentInput = button.parentElement?.querySelector<HTMLInputElement>("input");
      try {
        const result = await bridgeRequest<{ path: string | null }>("dialog.pickFile", {
          kind: button.dataset.browseKind,
          currentPath: currentInput?.value ?? ""
        });
        if (result.path) {
          setPath(path, result.path);
          render();
        }
      } catch (error) {
        showError(error);
      }
    });
  });

  document.querySelectorAll<HTMLSelectElement>("[data-gpu-target]").forEach(select => {
    select.addEventListener("change", () => {
      const index = select.value === "" ? null : Number(select.value);
      const path = select.dataset.gpuTarget === "sensevoice"
        ? "asr.senseVoice.vulkanDeviceIndex"
        : select.dataset.gpuTarget === "funasr-nano"
          ? "asr.funAsrNano.vulkanDeviceIndex"
          : "asr.whisperCpp.gpuDeviceIndex";
      setPath(path, index);
    });
  });

  document.querySelectorAll<HTMLButtonElement>("[data-refresh-gpu]").forEach(button => {
    button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        snapshot!.gpuDevices = await bridgeRequest<GpuDevice[]>("gpu.devices.get");
        render();
      } catch (error) {
        showError(error);
      }
    });
  });
}

function bindViewEvents(): void {
  if (activeView === "general") {
    document.querySelector<HTMLButtonElement>("#microphone-test-toggle")?.addEventListener("click", toggleMicrophoneTest);
    document.querySelector<HTMLButtonElement>("#reveal-config")?.addEventListener("click", async () => {
      try {
        await bridgeRequest("configuration.reveal");
      } catch (error) {
        showError(error);
      }
    });
  }

  if (activeView === "profiles") {
    bindProfileEvents();
  } else if (activeView === "models") {
    document.querySelector<HTMLDetailsElement>(".model-advanced")?.addEventListener("toggle", event => {
      advancedModelConfigurationOpen = (event.currentTarget as HTMLDetailsElement).open;
    });
    document.querySelectorAll<HTMLButtonElement>("[data-model-tab]").forEach(button => {
      button.addEventListener("click", () => {
        selectedModel = button.dataset.modelTab!;
        render();
      });
    });
    document.querySelectorAll<HTMLButtonElement>("[data-download-asset]").forEach(button => {
      button.addEventListener("click", () => void downloadModelAsset(button.dataset.downloadAsset!));
    });
    document.querySelectorAll<HTMLSelectElement>("[data-capability-variant-key]").forEach(select => {
      select.addEventListener("change", event => {
        const element = event.currentTarget as HTMLSelectElement;
        const selectionKey = element.dataset.capabilityVariantKey!;
        selectedCapabilityVariants[selectionKey] = element.value;
        const asset = getModelAssetStatus(element.value);
        if (asset?.installed) applyCapabilityPath(asset);
        render();
      });
    });
    document.querySelector<HTMLSelectElement>("#model-variant-select")?.addEventListener("change", event => {
      const assetId = (event.currentTarget as HTMLSelectElement).value;
      selectedModelVariants[selectedModel] = assetId;
      if (assetId !== "custom" && getModelAssetStatus(assetId)?.installed) {
        activateModelVariant(selectedModel, assetId);
      }
      render();
    });
    document.querySelector<HTMLButtonElement>("[data-download-model-variant]")?.addEventListener("click", buttonEvent => {
      const assetId = (buttonEvent.currentTarget as HTMLButtonElement).dataset.downloadModelVariant!;
      void downloadModelAsset(assetId, selectedModel);
    });
    document.querySelector<HTMLButtonElement>("#cancel-model-download")?.addEventListener("click", cancelModelDownload);
  } else if (activeView === "diagnostics") {
    bindDiagnosticEvents();
  }
}

async function toggleMicrophoneTest(): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>("#microphone-test-toggle");
  if (button) button.disabled = true;
  try {
    if (microphoneTestRunning) {
      microphoneTestRunning = false;
      microphoneLevels = [];
      render();
      await bridgeRequest<{ isRunning: boolean }>("microphone.test.stop");
    } else {
      const result = await bridgeRequest<{ isRunning: boolean; levels: MicrophoneLevel[] }>("microphone.test.start");
      microphoneTestRunning = result.isRunning;
      microphoneLevels = result.levels;
    }
    render();
  } catch (error) {
    showError(error);
    if (button) button.disabled = false;
  }
}

function activateModelVariant(model: string, assetId: string): void {
  if (model === "paraformer") {
    const item = Object.values(paraformerVariants).find(value => value.assetId === assetId);
    if (item) setPath("asr.paraformer.modelPath", item.modelPath);
  } else if (model === "senseVoice") {
    const item = Object.values(senseVoiceVariants).find(value => value.assetId === assetId);
    if (item) setPath("asr.senseVoice.modelPath", item.modelPath);
  } else if (model === "funAsrNano") {
    const item = Object.values(nanoLanguageModelVariants).find(value => value.assetId === assetId);
    if (item) setPath("asr.funAsrNano.languageModelPath", item.modelPath);
  }
}

function applyCapabilityPath(asset: ModelAssetStatus): void {
  if (asset.componentId === "encoder" && asset.providerId === "funasr-nano-gguf") {
    setPath("asr.funAsrNano.encoderModelPath", asset.targetPath);
  } else if (asset.componentId === "streaming-vad") {
    setPath("asr.streaming.sileroVadModelPath", asset.targetPath);
  } else if (asset.componentId === "punctuation") {
    setPath("asr.paraformer.punctuationModelPath", asset.targetPath);
  } else if (asset.componentId === "language-model") {
    setPath("asr.funAsrNano.languageModelPath", asset.targetPath);
  } else if (asset.componentId === "vad") {
    const path = asset.providerId === "paraformer-gguf"
      ? "asr.paraformer.vadModelPath"
      : asset.providerId === "sensevoice-gguf"
        ? "asr.senseVoice.vadModelPath"
        : "asr.funAsrNano.vadModelPath";
    setPath(path, asset.targetPath);
  }
}

async function downloadModelAsset(assetId: string, activateForModel: string | null = null): Promise<void> {
  try {
    const result = await bridgeRequest<{ completed: boolean; providerStatuses: ProviderStatus[]; modelAssets: ModelAssetStatus[] }>("model.asset.download.start", {
      assetId,
      asr: configuration!.asr
    });
    snapshot!.providerStatuses = result.providerStatuses;
    snapshot!.modelAssets = result.modelAssets;
    if (result.completed) {
      const asset = snapshot!.modelAssets.find(item => item.id === assetId);
      if (activateForModel) {
        selectedModelVariants[activateForModel] = assetId;
        activateModelVariant(activateForModel, assetId);
      } else if (asset) {
        applyCapabilityPath(asset);
      }
      showToast(t("{variant} installed and verified.", { variant: asset?.variant ?? assetId }));
    }
    render();
  } catch (error) {
    showError(error);
  }
}

async function cancelModelDownload(): Promise<void> {
  const button = document.querySelector<HTMLButtonElement>("#cancel-model-download");
  if (button) button.disabled = true;
  try {
    await bridgeRequest("model.download.cancel");
  } catch (error) {
    showError(error);
  }
}

function bindProfileEvents(): void {
  document.querySelectorAll<HTMLButtonElement>("[data-profile-tab]").forEach(button => {
    button.addEventListener("click", () => {
      activeProfileTab = button.dataset.profileTab as ProfileTab;
      render();
    });
  });
  document.querySelectorAll<HTMLButtonElement>("[data-profile-id]").forEach(button => {
    button.addEventListener("click", () => {
      selectedProfileId = button.dataset.profileId!;
      render();
    });
  });
  document.querySelector<HTMLButtonElement>("#use-profile")?.addEventListener("click", () => {
    void selectRuntimeProfile(getProfile().id);
  });
  document.querySelector<HTMLButtonElement>("#automatic-routing")?.addEventListener("click", () => {
    void selectRuntimeProfile(null);
  });
  document.querySelector<HTMLButtonElement>("#add-profile")?.addEventListener("click", addProfile);
  document.querySelector<HTMLButtonElement>("#duplicate-profile")?.addEventListener("click", duplicateProfile);
  document.querySelector<HTMLButtonElement>("#delete-profile")?.addEventListener("click", deleteProfile);
  document.querySelector<HTMLButtonElement>("#set-default")?.addEventListener("click", () => {
    configuration!.profiles.defaultProfileId = getProfile().id;
    markDirty();
    render();
  });
  document.querySelector<HTMLInputElement>("[data-profile-name-input]")?.addEventListener("change", event => {
    const input = event.currentTarget as HTMLInputElement;
    renameProfile(input.value);
  });
  document.querySelector<HTMLButtonElement>("#open-process-picker")?.addEventListener("click", () => {
    void openProcessPicker(getProfile());
  });
  document.querySelector<HTMLButtonElement>("#capture-gamepad-button")?.addEventListener("click", () => {
    void captureGamepadButton(getProfile());
  });
  document.querySelector<HTMLButtonElement>("#capture-mouse-button")?.addEventListener("click", () => {
    void captureMouseButton(getProfile());
  });
  document.querySelector<HTMLButtonElement>("#open-steamvr-bindings")?.addEventListener("click", async () => {
    try {
      await bridgeRequest("steamvr.openBindings");
      showToast(t("SteamVR controller bindings opened."));
    } catch (error) {
      showError(error);
    }
  });
  document.querySelector<HTMLButtonElement>("#refresh-steamvr-status")?.addEventListener("click", async event => {
    const button = event.currentTarget as HTMLButtonElement;
    button.disabled = true;
    button.classList.add("refreshing");
    try {
      snapshot!.steamVr = await bridgeRequest<SteamVrStatus>("steamvr.status.get");
      render();
    } catch (error) {
      button.disabled = false;
      button.classList.remove("refreshing");
      showError(error);
    }
  });
}

async function captureKeyboardChord(path: string): Promise<void> {
  if (keyboardCapturePath || mouseCaptureProfileId || gamepadCaptureProfileId) return;
  keyboardCapturePath = path;
  render();
  showToast(t("Press and release the keyboard shortcut now."));

  let resultMessage: string | null = null;
  let failure: unknown = null;
  try {
    const result = await bridgeRequest<{ virtualKeys: number[] }>("keyboard.capture");
    setPath(path, result.virtualKeys);
    resultMessage = t("Keyboard shortcut bound: {keys}", { keys: formatKeys(result.virtualKeys) });
  } catch (error) {
    failure = error;
  } finally {
    keyboardCapturePath = null;
    render();
  }

  if (resultMessage) showToast(resultMessage);
  if (failure) showError(failure);
}

async function captureMouseButton(profile: Profile): Promise<void> {
  if (keyboardCapturePath || mouseCaptureProfileId || gamepadCaptureProfileId) return;
  mouseCaptureProfileId = profile.id;
  render();
  showToast(t("Press and release a mouse button now."));

  let resultMessage: string | null = null;
  let failure: unknown = null;
  try {
    const result = await bridgeRequest<{ button: string }>("mouse.capture");
    const currentProfile = configuration?.profiles.items.find(item => item.id === profile.id);
    if (currentProfile) {
      currentProfile.input.mouse.button = result.button;
      markDirty();
      resultMessage = t("Mouse button bound: {button}", { button: formatMouseButton(result.button) });
    }
  } catch (error) {
    failure = error;
  } finally {
    mouseCaptureProfileId = null;
    render();
  }

  if (resultMessage) showToast(resultMessage);
  if (failure) showError(failure);
}

async function captureGamepadButton(profile: Profile): Promise<void> {
  if (keyboardCapturePath || mouseCaptureProfileId || gamepadCaptureProfileId) return;
  gamepadCaptureProfileId = profile.id;
  render();
  showToast(t("Press a button on any connected XInput controller."));

  let resultMessage: string | null = null;
  let failure: unknown = null;
  try {
    const result = await bridgeRequest<{ userIndex: number; buttonMask: number }>("gamepad.capture");
    const currentProfile = configuration?.profiles.items.find(item => item.id === profile.id);
    if (currentProfile) {
      currentProfile.input.gamepad.userIndex = result.userIndex;
      currentProfile.input.gamepad.buttonMask = result.buttonMask;
      markDirty();
      resultMessage = t("Gamepad button bound: controller {number}, {buttons}", {
        number: result.userIndex + 1,
        buttons: formatGamepadButtons(result.buttonMask)
      });
    }
  } catch (error) {
    failure = error;
  } finally {
    gamepadCaptureProfileId = null;
    render();
  }

  if (resultMessage) showToast(resultMessage);
  if (failure) showError(failure);
}

async function selectRuntimeProfile(profileId: string | null): Promise<void> {
  if (!snapshot || busy) return;
  busy = true;
  updateTopbar();
  try {
    const localConfiguration = configuration;
    const updated = await bridgeRequest<Snapshot>("runtime.profile.select", { profileId });
    snapshot = updated;
    configuration = dirty && localConfiguration
      ? localConfiguration
      : normalizeConfiguration(deepClone(updated.configuration));
    showToast(profileId ? t("Runtime switched to {profile}.", { profile: profileId }) : t("Automatic profile routing resumed."));
  } catch (error) {
    showError(error);
  } finally {
    busy = false;
    render();
  }
}

function bindDiagnosticEvents(): void {
  document.querySelector<HTMLSelectElement>("#output-test-profile")?.addEventListener("change", event => {
    diagnosticProfileId = (event.currentTarget as HTMLSelectElement).value;
    diagnosticTargetProcessId = null;
    render();
  });
  document.querySelector<HTMLSelectElement>("#output-test-target")?.addEventListener("change", event => {
    diagnosticTargetProcessId = Number((event.currentTarget as HTMLSelectElement).value);
  });
  document.querySelector<HTMLButtonElement>("#output-test")?.addEventListener("click", async () => {
    const profileId = document.querySelector<HTMLSelectElement>("#output-test-profile")?.value;
    const text = document.querySelector<HTMLInputElement>("#output-test-message")?.value;
    const targetValue = document.querySelector<HTMLSelectElement>("#output-test-target")?.value;
    if (!profileId) {
      return;
    }
    try {
      await bridgeRequest("diagnostic.outputTest", {
        profileId,
        text,
        targetProcessId: targetValue ? Number(targetValue) : null
      });
      showToast(t("Output test sent."));
    } catch (error) {
      showError(error);
    }
  });
  void refreshDiagnosticMetrics();
  if (diagnosticMetricsTimer === null) {
    diagnosticMetricsTimer = window.setInterval(() => void refreshDiagnosticMetrics(), 2000);
  }
  document.querySelector<HTMLButtonElement>("#refresh-snapshot")?.addEventListener("click", loadSnapshot);
  document.querySelector<HTMLButtonElement>("#open-log-location")?.addEventListener("click", async () => {
    try {
      await bridgeRequest("logs.reveal");
    } catch (error) {
      showError(error);
    }
  });
  document.querySelector<HTMLButtonElement>("#clear-logs")?.addEventListener("click", () => {
    snapshot!.logs = [];
    render();
  });
}

async function refreshDiagnosticMetrics(): Promise<void> {
  if (!snapshot || activeView !== "diagnostics") return;
  try {
    snapshot.diagnostics = await bridgeRequest<DiagnosticMetrics>("diagnostic.metrics.get");
    const memory = document.querySelector<HTMLElement>("#diagnostic-memory");
    const average = document.querySelector<HTMLElement>("#diagnostic-average");
    if (memory) memory.textContent = formatBytes(snapshot.diagnostics.workingSetBytes);
    if (average) {
      average.textContent = snapshot.diagnostics.averageRecognitionMilliseconds === null
        ? t("No samples")
        : `${Math.round(snapshot.diagnostics.averageRecognitionMilliseconds)} ms · ${t("{count} samples", { count: snapshot.diagnostics.recognitionCount })}`;
    }
  } catch {
    // The next scheduled refresh retries while the diagnostics view remains open.
  }
}

function uniqueProfileName(base: string): string {
  const existing = new Set(configuration!.profiles.items.map(profile => profile.id.toLowerCase()));
  let candidate = base;
  let suffix = 2;
  while (existing.has(candidate.toLowerCase())) {
    candidate = `${base}-${suffix++}`;
  }
  return candidate;
}

function addProfile(): void {
  const source = getProfile();
  const profile = deepClone(source);
  profile.id = uniqueProfileName(t("New profile"));
  delete profile.displayName;
  delete profile.builtInTemplate;
  profile.builtIn = false;
  profile.match.processNames = [];
  configuration!.profiles.items.push(profile);
  selectedProfileId = profile.id;
  markDirty();
  render();
}

function duplicateProfile(): void {
  const source = getProfile();
  const profile = deepClone(source);
  profile.id = uniqueProfileName(t("{name} copy", { name: source.id }));
  delete profile.displayName;
  delete profile.builtInTemplate;
  profile.builtIn = false;
  configuration!.profiles.items.push(profile);
  selectedProfileId = profile.id;
  markDirty();
  render();
}

function deleteProfile(): void {
  const profile = getProfile();
  if (profile.builtIn) {
    return;
  }
  configuration!.profiles.items = configuration!.profiles.items.filter(item => item.id !== profile.id);
  if (configuration!.profiles.defaultProfileId === profile.id) {
    configuration!.profiles.defaultProfileId = configuration!.profiles.items[0].id;
  }
  selectedProfileId = configuration!.profiles.defaultProfileId;
  markDirty();
  render();
}

function renameProfile(value: string): void {
  const normalized = value.trim();
  const profile = getProfile();
  if (!normalized) {
    showToast(t("Profile name is required."), true);
    const input = document.querySelector<HTMLInputElement>("[data-profile-name-input]");
    if (input) input.value = profile.id;
    return;
  }
  if (configuration!.profiles.items.some(item => item !== profile && item.id.toLowerCase() === normalized.toLowerCase())) {
    showToast(t("Profile name must be unique."), true);
    const input = document.querySelector<HTMLInputElement>("[data-profile-name-input]");
    if (input) input.value = profile.id;
    return;
  }
  if (normalized === profile.id) return;
  const oldId = profile.id;
  profile.id = normalized;
  delete profile.displayName;
  if (configuration!.profiles.defaultProfileId === oldId) {
    configuration!.profiles.defaultProfileId = normalized;
  }
  if (snapshot!.runtime.profileOverride === oldId) {
    snapshot!.runtime.profileOverride = normalized;
  }
  selectedProfileId = normalized;
  markDirty();
  const profileButton = Array.from(document.querySelectorAll<HTMLButtonElement>("[data-profile-id]"))
    .find(button => button.dataset.profileId === oldId);
  if (profileButton) {
    profileButton.dataset.profileId = normalized;
    const name = profileButton.querySelector<HTMLElement>(".profile-name");
    if (name) name.textContent = normalized;
  }
  const routingLabel = document.querySelector<HTMLElement>(".routing-label");
  if (routingLabel && snapshot!.runtime.profileOverride === normalized) {
    routingLabel.textContent = t("Runtime: {profile}", { profile: normalized });
  }
}

function formatKeys(keys: number[]): string {
  return keys.length === 0 ? t("Not configured") : keys.map(formatVirtualKey).join(" + ");
}

function formatGamepadButtons(buttonMask: number): string {
  const names = gamepadButtonNames(buttonMask);
  return names.length === 0 ? t("Not configured") : names.map(name => t(name)).join(" + ");
}

function formatMouseButton(button: string): string {
  const names: Record<string, string> = {
    left: "Left mouse button",
    right: "Right mouse button",
    middle: "Middle mouse button",
    x1: "Side button 1",
    x2: "Side button 2"
  };
  return t(names[button.toLowerCase()] ?? button);
}

function formatVirtualKey(key: number): string {
  const names: Record<number, string> = {
    8: "Backspace",
    9: "Tab",
    12: "Clear",
    13: "Enter",
    16: "Shift",
    17: "Ctrl",
    18: "Alt",
    19: "Pause",
    20: "Caps Lock",
    21: "IME Kana",
    23: "IME Junja",
    24: "IME Final",
    25: "IME Hanja",
    27: "Esc",
    28: "IME Convert",
    29: "IME Nonconvert",
    30: "IME Accept",
    31: "IME Mode Change",
    32: "Space",
    33: "Page Up",
    34: "Page Down",
    35: "End",
    36: "Home",
    37: "Left",
    38: "Up",
    39: "Right",
    40: "Down",
    41: "Select",
    42: "Print",
    43: "Execute",
    44: "Print Screen",
    45: "Insert",
    46: "Delete",
    47: "Help",
    91: "Left Windows",
    92: "Right Windows",
    93: "Menu",
    95: "Sleep",
    106: "Numpad Multiply",
    107: "Numpad Add",
    108: "Numpad Separator",
    109: "Numpad Subtract",
    110: "Numpad Decimal",
    111: "Numpad Divide",
    144: "Num Lock",
    145: "Scroll Lock",
    160: "Left Shift",
    161: "Right Shift",
    162: "Left Ctrl",
    163: "Right Ctrl",
    164: "Left Alt",
    165: "Right Alt",
    166: "Browser Back",
    167: "Browser Forward",
    168: "Browser Refresh",
    169: "Browser Stop",
    170: "Browser Search",
    171: "Browser Favorites",
    172: "Browser Home",
    173: "Volume Mute",
    174: "Volume Down",
    175: "Volume Up",
    176: "Next Track",
    177: "Previous Track",
    178: "Stop Media",
    179: "Play/Pause",
    180: "Launch Mail",
    181: "Select Media",
    182: "Launch App 1",
    183: "Launch App 2",
    186: "Semicolon",
    187: "Equals",
    188: "Comma",
    189: "Minus",
    190: "Period",
    191: "Slash",
    192: "Backquote",
    219: "Left Bracket",
    220: "Backslash",
    221: "Right Bracket",
    222: "Quote",
    223: "OEM 8",
    226: "OEM 102",
    229: "IME Process",
    231: "Packet",
    246: "Attn",
    247: "CrSel",
    248: "ExSel",
    249: "Erase EOF",
    250: "Play",
    251: "Zoom",
    252: "NoName",
    253: "PA1",
    254: "OEM Clear"
  };
  if (names[key]) return t(names[key]);
  if (key >= 48 && key <= 57) return String.fromCharCode(key);
  if (key >= 65 && key <= 90) return String.fromCharCode(key);
  if (key >= 96 && key <= 105) return t("Numpad {number}", { number: key - 96 });
  if (key >= 112 && key <= 135) return `F${key - 111}`;
  return `VK 0x${key.toString(16).padStart(2, "0").toUpperCase()}`;
}

document.addEventListener("keydown", event => {
  if (processPickerProfileId && event.code === "Escape") {
    event.preventDefault();
    closeProcessPicker();
  }
});

async function toggleRuntime(): Promise<void> {
  if (!snapshot || busy) return;
  busy = true;
  updateTopbar();
  try {
    const operation = snapshot.runtime.isRunning ? "runtime.stop" : "runtime.start";
    const result = await bridgeRequest<{ isRunning: boolean }>(operation);
    snapshot.runtime.isRunning = result.isRunning;
  } catch (error) {
    showError(error);
  } finally {
    busy = false;
    updateTopbar();
  }
}

async function saveConfiguration(): Promise<void> {
  if (!configuration || !snapshot || !dirty) return;
  if (busy || saveInProgress) {
    scheduleAutoSave(500);
    return;
  }

  saveInProgress = true;
  const revision = configurationRevision;
  const localConfiguration = configuration;
  const configurationToSave = deepClone(configuration);
  updateTopbar();
  let failure: unknown = null;
  let retryDelay = 400;
  try {
    const updated = await bridgeRequest<Snapshot>("configuration.save", { configuration: configurationToSave });
    snapshot = updated;
    if (configurationRevision === revision) {
      configuration = normalizeConfiguration(deepClone(updated.configuration));
      dirty = false;
      autoSaveFailureCount = 0;
      applyUiLanguage(configuration.application.uiLanguage);
    } else {
      configuration = localConfiguration;
    }
  } catch (error) {
    failure = error;
    autoSaveFailureCount++;
    retryDelay = 5000;
  } finally {
    saveInProgress = false;
    updateTopbar();
  }

  if (failure) showError(failure);
  if (dirty && (!failure || autoSaveFailureCount < 3)) scheduleAutoSave(retryDelay);
}

function showError(error: unknown): void {
  showToast(error instanceof Error ? error.message : String(error), true);
}

function showToast(message: string, error = false): void {
  const region = document.querySelector<HTMLElement>("#toast-region");
  if (!region) return;
  const toast = document.createElement("div");
  toast.className = `toast ${error ? "error" : ""}`;
  toast.innerHTML = `${icon(error ? "alertCircleOutline" : "check")}<span>${escapeHtml(message)}</span>`;
  region.appendChild(toast);
  window.setTimeout(() => toast.remove(), 3200);
}

async function loadSnapshot(): Promise<void> {
  try {
    const loaded = await bridgeRequest<Snapshot>("snapshot.get");
    snapshot = loaded;
    microphoneTestRunning = loaded.microphoneTest.isRunning;
    microphoneLevels = loaded.microphoneTest.levels;
    modelDownload = loaded.modelDownload;
    configuration = normalizeConfiguration(deepClone(loaded.configuration));
    applyUiLanguage(configuration.application.uiLanguage);
    if (!selectedProfileId || !configuration.profiles.items.some(profile => profile.id === selectedProfileId)) {
      selectedProfileId = configuration.profiles.defaultProfileId;
    }
    dirty = false;
    configurationRevision = 0;
    autoSaveFailureCount = 0;
    if (autoSaveTimer !== null) {
      window.clearTimeout(autoSaveTimer);
      autoSaveTimer = null;
    }
    render();
  } catch (error) {
    reportWebUiError(
      error instanceof Error ? error.message : String(error),
      error instanceof Error ? error.stack : undefined);
    root.innerHTML = `<div class="empty-state">${escapeHtml(error instanceof Error ? error.message : error)}</div>`;
  } finally {
    await signalUiReady();
  }
}

async function signalUiReady(): Promise<void> {
  await new Promise<void>(resolve => window.setTimeout(resolve, 0));
  try {
    await bridgeRequest("ui.ready");
  } catch {
    // The rendered page remains useful even if the native lifecycle handshake is unavailable.
  }
}

const nativeLifecycleWindow = window as NativeLifecycleWindow;
nativeLifecycleWindow.vrchatVoiceInputPrepareClose = (): string => {
  if (autoSaveTimer !== null) {
    window.clearTimeout(autoSaveTimer);
    autoSaveTimer = null;
  }
  root.style.pointerEvents = "none";
  const state: NativeCloseState = {
    dirty,
    saveInProgress,
    busy,
    configuration: configuration ? deepClone(configuration) : null
  };
  return JSON.stringify(state);
};
nativeLifecycleWindow.vrchatVoiceInputCancelClose = (): void => {
  root.style.pointerEvents = "";
  if (dirty) scheduleAutoSave();
};

void loadSnapshot();
