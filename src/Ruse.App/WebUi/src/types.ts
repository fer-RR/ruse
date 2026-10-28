export type NodeKind = 'source' | 'output' | 'modifier';
export type LinkKind = 'direct' | 'source-modifier' | 'modifier-modifier' | 'modifier-output';
export type CatalogKind = 'app' | 'system' | 'output' | 'input' | 'virtual' | 'effect' | 'chain' | 'file-player' | 'recorder' | 'vst3';

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
  defaultOutputId: string;
  defaultOutputName: string;
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
}

export interface NodeModel {
  id: string;
  kind: NodeKind;
  flavor: string;
  title: string;
  subtitle: string;
  accent: string;
  x: number;
  y: number;
  volume: number;
  muted: boolean;
  micHint?: string;
  deviceId?: string;
  parameters: ParameterModel[];
  enabled?: boolean;
  isWaveform?: boolean;
  isFilePlayer?: boolean;
  filePath?: string;
  fileLoop?: boolean;
  isDbMeter?: boolean;
  isTranscriber?: boolean;
  transcriberModel?: string;
  isTranslator?: boolean;
  translatorTarget?: string;
  translatorModel?: string;
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
  recorderMode?: string;
  recorderAutoSave?: boolean;
  chainName?: string;
  chainElements?: string[];
  duplicateRisk?: boolean;
  duplicateWarning?: string;
  warningSeverity?: 'warning' | 'info' | string;
}

export interface LinkModel {
  id: string;
  from: string;
  to: string;
  kind: LinkKind;
  gain: number;
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
  clipping: boolean;
  filePlaying?: boolean;
  recordingStatus?: string;
  recordingSeconds?: number;
  recordingError?: string;
  waveform?: number[];
  db?: number;
  peakDb?: number;
  dbMeterVertical?: boolean;
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
  skippedFrames: number;
  outputCount: number;
  outputsRunning: number;
  worstLatencyMs: number;
  chainLatencyMs: number;
  shortReadFrames: number;
  lowLatency: boolean;
  telemetryMs: number;
  audioFormat: string;
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
  installed: boolean;
  installedSize?: string;
  license?: string;
  note?: string;
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
  startRoutingOnLaunch: boolean;
  highlightMutedNodes: boolean;
  signalGlow: boolean;
  snapToGrid: boolean;
  highlightCableOnHover: boolean;
  cableAnimation: boolean;
  minimapMode: number;
  startWithWindows: boolean;
  showWelcomeScreen: boolean;
  minimizeToTray: boolean;
  showDiagnostics: boolean;
  transcriberModel: string;
  transcriberModels: TranscriberModelInfo[];
  transcriberDownload?: TranscriberDownload | null;
  translatorModel: string;
  translatorTarget: string;
  translatorModels: TranslatorModelInfo[];
  translatorDownload?: TranslatorDownload | null;
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
