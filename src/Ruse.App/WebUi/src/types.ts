export type NodeKind = 'source' | 'output' | 'modifier';
export type LinkKind = 'direct' | 'source-modifier' | 'modifier-modifier' | 'modifier-output';
export type CatalogKind = 'app' | 'system' | 'output' | 'input' | 'virtual' | 'effect' | 'chain' | 'recorder';

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
  active?: boolean;
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
  parameters: ParameterModel[];
  enabled?: boolean;
  isWaveform?: boolean;
  isTranscriber?: boolean;
  transcriberModel?: string;
  isRecorder: boolean;
  recordingPath: string;
  recordingFormat: string;
  chainName?: string;
  chainElements?: string[];
}

export interface LinkModel {
  id: string;
  from: string;
  to: string;
  kind: LinkKind;
  gain: number;
}

export interface GraphSnapshot {
  version: number;
  nodes: NodeModel[];
  links: LinkModel[];
  running: boolean;
}

export interface NodeTelemetry {
  level: number;
  clipping: boolean;
  recordingStatus?: string;
  recordingSeconds?: number;
  recordingError?: string;
  waveform?: number[];
  transcriptionStatus?: string;
  transcript?: string;
  partialTranscript?: string;
}

export interface TelemetrySnapshot {
  meters: Record<string, NodeTelemetry>;
  running: boolean;
  clipping: boolean;
  masterGain: number;
  masterMuted: boolean;
  micMuted: boolean;
}

export interface TranscriberModelInfo {
  id: string;
  language: string;
  name: string;
  sizeMb: number;
  installed: boolean;
}

export interface SettingsModel {
  startRoutingOnLaunch: boolean;
  highlightMutedNodes: boolean;
  signalGlow: boolean;
  highlightCableOnHover: boolean;
  minimapMode: number;
  startWithWindows: boolean;
  transcriberModel: string;
  transcriberModels: TranscriberModelInfo[];
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
