export type NodeKind = 'source' | 'output' | 'modifier';
export type LinkKind = 'direct' | 'source-modifier' | 'modifier-modifier' | 'modifier-output' | 'sidechain';
export type CatalogKind = 'app' | 'system' | 'tone' | 'tts' | 'network-in' | 'network-out' | 'output' | 'input' | 'virtual' | 'effect' | 'chain' | 'file-player' | 'recorder' | 'vst3' | 'template';

export interface ChainElement {
  typeId: string;
  parameters: Record<string, number>;
}

export interface CatalogItem {
  id: string;
  kind: CatalogKind;
  name: string;
  subtitle: string;
  details: string;
  processId?: number;
  processName?: string;
  active?: boolean;
  deviceId?: string;
  deviceName?: string;
  direction?: string;
  provider?: string;
  virtual?: boolean;
  isDefault?: boolean;
  badge?: string;
  micHint?: string;
  iconDataUrl?: string;
  parameters?: ParameterModel[];
  elements?: ChainElement[];
}

export interface CatalogSnapshot {
  apps: CatalogItem[];
  outputs: CatalogItem[];
  inputs: CatalogItem[];
  virtuals: CatalogItem[];
  effects: CatalogItem[];
  chains: CatalogItem[];
  vst3Plugins: CatalogItem[];
  frameTemplates: CatalogItem[];
  defaultOutputId: string;
  defaultOutputName: string;
  perAppCaptureAvailable: boolean;
  perAppCaptureMessage?: string;
}

export interface ParameterModel {
  processorIndex: number;
  parameterIndex: number;
  name: string;
  min: number;
  max: number;
  value: number;
  unit: string;
  logarithmic: boolean;
  isToggle: boolean;
  isInteger: boolean;
  isChoice: boolean;
  options: string[];
  midiBinding?: string;
  midiLearning?: boolean;
}

export interface NodeModel {
  id: string;
  controlId?: string;
  kind: NodeKind;
  flavor: string;
  title: string;
  subtitle: string;
  accent: string;
  x: number;
  y: number;
  volume: number;
  muted: boolean;
  solo?: boolean;
  color?: string;
  supportsSidechain?: boolean;
  micHint?: string;
  deviceId?: string;
  sampleRate?: number;
  bitsPerSample?: number;
  showDiagnostics?: boolean;
  midiEnabled?: boolean;
  parameters: ParameterModel[];
  enabled?: boolean;
  isSpectrum?: boolean;
  isSpectrogram?: boolean;
  isWaveform?: boolean;
  isGraphicEq?: boolean;
  isParametricEq?: boolean;
  eqBandTypes?: string[];
  isConvolutionReverb?: boolean;
  irPath?: string;
  irName?: string;
  irError?: string;
  isNoiseSuppression?: boolean;
  noiseProfilePath?: string;
  noiseProfileName?: string;
  noiseProfileError?: string;
  isParallel?: boolean;
  send?: number;
  isFilePlayer?: boolean;
  filePath?: string;
  fileOutputId?: string;
  fileOutputs?: Array<{ id: string; name: string }>;
  fileLoop?: boolean;
  fileSpeed?: number;
  isToneGenerator?: boolean;
  toneFrequency?: number;
  toneWaveform?: string;
  toneLevelDb?: number;
  isNoteDetector?: boolean;
  isNetworkInput?: boolean;
  isNetworkOutput?: boolean;
  networkHost?: string;
  networkPort?: number;
  networkLocalIp?: string;
  networkError?: string;
  networkBufferedMs?: number;
  isTts?: boolean;
  ttsText?: string;
  ttsSourceNodeId?: string;
  ttsModel?: string;
  ttsSpeed?: number;
  ttsVoice?: number;
  ttsVoices?: string[];
  ttsUsesGpu?: boolean;
  ttsStatus?: string;
  ttsModelInstalled?: boolean;
  ttsSources?: Array<{ id: string; title: string; kind: string }>;
  isDbMeter?: boolean;
  isLoudness?: boolean;
  isTranscriber?: boolean;
  transcriberModel?: string;
  isTranslator?: boolean;
  translatorTarget?: string;
  translatorModel?: string;
  translatorProvider?: string;
  translatorModels?: TranslatorModelInfo[];
  isVst3?: boolean;
  vst3Identifier?: string;
  vst3Status?: string;
  vst3Vendor?: string;
  vst3Version?: string;
  vst3Error?: string;
  vst3HasEditor?: boolean;
  vst3EditorOpen?: boolean;
  vst3LatencyMs?: number;
  isRecorder: boolean;
  recordingPath: string;
  recordingFormat: string;
  recorderSampleRate?: number;
  recorderMode?: string;
  recorderAutoSave?: boolean;
  recorderSilenceStopSeconds?: number;
  recorderStartThresholdDb?: number;
  chainName?: string;
  chainElements?: string[];
  duplicateRisk?: boolean;
  duplicateWarning?: string;
  warningSeverity?: 'warning' | 'info' | string;
}

export interface NodeStateUpdate {
  id: string;
  volume?: number;
  muted?: boolean;
  solo?: boolean;
  enabled?: boolean;
  send?: number;
  parallel?: boolean;
  color?: string;
  accent?: string;
}

export interface LinkStateUpdate {
  id: string;
  gain: number;
  enabled: boolean;
  native: boolean;
  nativeRate: number;
}

export interface LinkModel {
  id: string;
  from: string;
  to: string;
  kind: LinkKind;
  gain: number;
  enabled?: boolean;
  native?: boolean;
  nativeRate?: number;
}

export interface GraphWarning {
  sourceId: string;
  outputId: string;
  deviceName: string;
  message: string;
  severity: 'warning' | 'info' | string;
}

export interface GraphSnapshot {
  version: number;
  nodes: NodeModel[];
  links: LinkModel[];
  frames?: FrameModel[];
  warnings?: GraphWarning[];
  running: boolean;
}

export interface FrameModel {
  id: string;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FirstRouteResult {
  sourceId: string;
  sourceTitle: string;
  outputTitle: string;
  warning?: string;
  severity?: 'warning' | 'info' | string;
}

export interface NodeTelemetry {
  level: number;
  levelLeft?: number;
  levelRight?: number;
  meterDbLeft?: number;
  meterDbRight?: number;
  clipping: boolean;
  clipGuardActive?: boolean;
  filePlaying?: boolean;
  filePaused?: boolean;
  filePositionSeconds?: number;
  fileDurationSeconds?: number;
  recordingStatus?: string;
  recordingSeconds?: number;
  recordingError?: string;
  spectrum?: number[];
  spectrumPeakHz?: number;
  spectrogram?: number[];
  spectrogramPeakHz?: number;
  spectrogramColumns?: number;
  spectrogramNewColumns?: number;
  spectrogramTotalColumns?: number;
  spectrogramFull?: boolean;
  spectrogramWindowSeconds?: number;
  waveform?: number[];
  waveformPeak?: number;
  db?: number;
  peakDb?: number;
  dbLeft?: number;
  dbRight?: number;
  peakDbLeft?: number;
  peakDbRight?: number;
  dbMeterVertical?: boolean;
  loudnessIntegrated?: number;
  loudnessShortTerm?: number;
  loudnessRange?: number;
  loudnessTruePeak?: number;
  transcriptionStatus?: string;
  transcript?: string;
  partialTranscript?: string;
  translatedTranscript?: string;
  translatedPartial?: string;
  detectedLanguage?: string;
  transcriptionError?: string;
  transcriptionLevel?: number;
  transcriptionHasAudio?: boolean;
  transcriptionElapsed?: number;
  usesGpu?: boolean;
  noteName?: string;
  noteMidi?: number;
  noteCents?: number;
  noteHz?: number;
  noteConfidence?: number;
  noteActive?: boolean;
}

export interface DiagnosticInfo {
  cpuPercent: number;
  cpuShare: number;
  gpuPercent?: number | null;
  renderTier: number;
  workingSetMb: number;
  managedMb: number;
  threads: number;
  processUptimeSeconds: number;
  nodeCount: number;
  linkCount: number;
  sourceCount: number;
  sourcesRunning: number;
  worstBacklogMs: number;
  sourceFillRateMsPerSec: number;
  outputRingFillPercent: number;
  underrunsPerMinute: number;
  skippedFramesPerMinute: number;
  latencyMeanMs: number;
  latencyStdDevMs: number;
  estimatedDriftPpm: number;
  skippedFrames: number;
  outputCount: number;
  outputsRunning: number;
  worstLatencyMs: number;
  engineBlockMs: number;
  outputTargetMs: number;
  outputBufferedMs: number;
  endpointLatencyMs: number;
  engineQueueMs: number;
  chainLatencyMs: number;
  shortReadFrames: number;
  outputUnderruns: number;
  lowLatency: boolean;
  telemetryMs: number;
  audioFormat: string;
  gpuAvailable?: boolean;
  gpuDeviceName?: string;
  gpuStatusError?: string;
  gpuFallbackReason?: string;
}

export interface TelemetrySnapshot {
  meters: Record<string, NodeTelemetry>;
  running: boolean;
  clipping: boolean;
  masterGain: number;
  masterMuted: boolean;
  micMuted: boolean;
  diagnostics?: DiagnosticInfo | null;
}

export interface TranscriberModelInfo {
  id: string;
  language: string;
  name: string;
  tier: string;
  sizeMb: number;
  vramMb?: number;
  installed: boolean;
  installedSize?: string;
  note?: string;
}

export interface TranscriberDownload {
  id: string;
  phase: string;
  percent: number;
  message: string;
}

export interface TranslatorModelInfo {
  id: string;
  name: string;
  tier: string;
  sizeMb: number;
  vramMb?: number;
  installed: boolean;
  installedSize?: string;
  license?: string;
  note?: string;
}

export interface OpusModelInfo {
  id: string;
  name: string;
  source: string;
  target: string;
  sizeMb: number;
  installed: boolean;
  installedSize?: string;
  license?: string;
  note?: string;
}

export interface TtsModelInfo {
  id: string;
  name: string;
  languages: string;
  sizeMb: number;
  installed: boolean;
  installedSize?: string;
  license?: string;
  note?: string;
}

export interface TtsDownload {
  id: string;
  phase: string;
  percent: number;
  message: string;
}
export interface TranslatorDownload {
  id: string;
  phase: string;
  percent: number;
  message: string;
}

export interface SettingsModel {
  autoLoadSession: boolean;
  followPlayingApps: boolean;
  followOutputId: string;
  uiLanguage: string;
  theme: 'dark' | 'light';
  startRoutingOnLaunch: boolean;
  highlightMutedNodes: boolean;
  signalGlow: boolean;
  snapToGrid: boolean;
  highlightCableOnHover: boolean;
  cableAnimation: boolean;
  connectorAnimation: boolean;
  minimapMode: number;
  startWithWindows: boolean;
  showWelcomeScreen: boolean;
  minimizeToTray: boolean;
  showDiagnostics: boolean;
  controlApiEnabled: boolean;
  controlApiPort: number;
  controlApiUrl?: string;
  controlApiToken?: string;
  aggressiveOutputCushion: boolean;
  clipGuard?: boolean;
  lowLatencyWasapi?: boolean;
  audioAutoMode?: boolean;
  audioBlockSizeMs?: number;
  outputBufferMode?: string;
  outputBufferManualMs?: number;
  engineCushionMode?: string;
  engineCushionManualMs?: number;
  elasticRing?: boolean;
  useGpu: boolean;
  isolateVst3: boolean;
  midiEnabled?: boolean;
  midiInputDevice?: string;
  midiOpen?: boolean;
  midiError?: string;
  midiInputs?: Array<{ name: string }>;
  gpuAvailable?: boolean;
  gpuDeviceName?: string;
  gpuStatusError?: string;
  gpuFallbackReason?: string;
  transcriberModel: string;
  transcriberModels: TranscriberModelInfo[];
  transcriberDownload?: TranscriberDownload | null;
  translatorModel: string;
  translatorTarget: string;
  translatorModels: TranslatorModelInfo[];
  translationProvider: string;
  ttsModel: string;
  ttsSpeed?: number;
  ttsModels: TtsModelInfo[];
  ttsDownload?: TtsDownload | null;
  installedTtsModelsSize?: string;
  installedTtsModelsCount?: number;
  opusModels: OpusModelInfo[];
  translatorDownload?: TranslatorDownload | null;
  installedOpusModelsSize?: string;
  installedOpusModelsCount?: number;
  installedTranslatorModelsSize?: string;
  installedTranslatorModelsCount?: number;
  installedModelsSize?: string;
  installedModelsCount?: number;
  appVersion?: string;
  dotNetVersion?: string;
  webView2Version?: string;
  renderTier?: number;
  dataFolder?: string;
  modelsFolder?: string;
}

export interface StatusMessage {
  text: string;
  isError: boolean;
}

export interface HistorySnapshot {
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string;
  redoLabel: string;
}

export interface ChainEditorRequest {
  name: string;
  previousName?: string;
  nodeId?: string;
  elements: ChainElement[];
}
