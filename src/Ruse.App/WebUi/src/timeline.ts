export interface TimelineAutomationPoint {
  sample: number;
  value: number;
  curve: number;
}

export interface TimelineAutomationLaneModel {
  id: string;
  targetNodeId: string;
  processorIndex: number;
  parameterIndex: number;
  points: TimelineAutomationPoint[];
}

export interface TimelineClipModel {
  id: string;
  name: string;
  sourcePath: string;
  startSample: number;
  lengthSamples: number;
  sourceOffsetSamples: number;
  gain: number;
  pitchSemitones: number;
  fadeInSamples: number;
  fadeOutSamples: number;
  peaks: number[];
}

export interface TimelineTrackModel {
  id: string;
  name: string;
  sourceNodeId?: string;
  generatorNodeId?: string;
  gain: number;
  muted: boolean;
  solo: boolean;
  clips: TimelineClipModel[];
  automation: TimelineAutomationLaneModel[];
}

export interface TimelineProjectModel {
  version: number;
  sampleRate: number;
  tempo: number;
  loopStartSample: number;
  loopEndSample: number;
  tracks: TimelineTrackModel[];
}

export interface TimelineState {
  project: TimelineProjectModel;
  playheadSample: number;
  playing: boolean;
  sampleRate: number;
  canUndo?: boolean;
  canRedo?: boolean;
  recording?: boolean;
  metronome?: boolean;
  countInBeats?: number;
  bouncing?: boolean;
  bounceProgress?: number;
}

export interface TimelineAutomationTarget {
  nodeId: string;
  trackId?: string;
  label: string;
  processorIndex: number;
  parameterIndex: number;
  min: number;
  max: number;
  value: number;
}

