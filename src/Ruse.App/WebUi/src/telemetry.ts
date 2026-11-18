import { useCallback, useSyncExternalStore } from 'react';
import type { DiagnosticInfo, NodeTelemetry, TelemetrySnapshot } from './types';

const emptyTelemetry: TelemetrySnapshot = {
  meters: {}, running: false, clipping: false,
  masterGain: 1, masterMuted: false, micMuted: false,
};

let snapshot = emptyTelemetry;
let uiSnapshot = { running: false, micMuted: false, masterMuted: false, masterGain: 1 };
let recordingSnapshot = { count: 0, seconds: 0 };
const globalListeners = new Set<() => void>();
const uiListeners = new Set<() => void>();
const recordingListeners = new Set<() => void>();
const nodeListeners = new Map<string, Set<() => void>>();
const activeListeners = new Map<string, Set<() => void>>();
const nodeActive = new Map<string, boolean>();
const titleListeners = new Map<string, Set<() => void>>();
const titleLevels = new Map<string, number>();
const titleActive = new Map<string, boolean>();
let nodeTitles: Record<string, string> = {};

export function sameNode(a: NodeTelemetry | undefined, b: NodeTelemetry | undefined): boolean {
  if (a === b) return true;
  if (!a || !b) return false;
  return a.level === b.level &&
    a.levelLeft === b.levelLeft &&
    a.levelRight === b.levelRight &&
    a.meterDbLeft === b.meterDbLeft &&
    a.meterDbRight === b.meterDbRight &&
    a.clipping === b.clipping &&
    a.clipGuardActive === b.clipGuardActive &&
    a.filePlaying === b.filePlaying &&
    a.filePaused === b.filePaused &&
    a.filePositionSeconds === b.filePositionSeconds &&
    a.fileDurationSeconds === b.fileDurationSeconds &&
    a.recordingStatus === b.recordingStatus &&
    a.recordingSeconds === b.recordingSeconds &&
    a.recordingError === b.recordingError &&
    a.db === b.db &&
    a.peakDb === b.peakDb &&
    a.dbLeft === b.dbLeft &&
    a.dbRight === b.dbRight &&
    a.peakDbLeft === b.peakDbLeft &&
    a.peakDbRight === b.peakDbRight &&
    a.dbMeterVertical === b.dbMeterVertical &&
    a.transcriptionStatus === b.transcriptionStatus &&
    a.transcript === b.transcript &&
    a.partialTranscript === b.partialTranscript &&
    a.translatedTranscript === b.translatedTranscript &&
    a.translatedPartial === b.translatedPartial &&
    a.detectedLanguage === b.detectedLanguage &&
    a.transcriptionError === b.transcriptionError &&
    a.transcriptionLevel === b.transcriptionLevel &&
    a.transcriptionHasAudio === b.transcriptionHasAudio &&
    a.transcriptionElapsed === b.transcriptionElapsed &&
    a.usesGpu === b.usesGpu &&
    a.noteName === b.noteName &&
    a.noteMidi === b.noteMidi &&
    a.noteCents === b.noteCents &&
    a.noteHz === b.noteHz &&
    a.noteConfidence === b.noteConfidence &&
    a.noteActive === b.noteActive &&
    a.inputLatencyMs === b.inputLatencyMs &&
    a.outputLatencyMs === b.outputLatencyMs &&
    sameArray(a.spectrum, b.spectrum) &&
    a.spectrumPeakHz === b.spectrumPeakHz &&
    sameArray(a.spectrogram, b.spectrogram) &&
    a.spectrogramPeakHz === b.spectrogramPeakHz &&
    a.spectrogramColumns === b.spectrogramColumns &&
    a.spectrogramNewColumns === b.spectrogramNewColumns &&
    a.spectrogramTotalColumns === b.spectrogramTotalColumns &&
    a.spectrogramFull === b.spectrogramFull &&
    a.spectrogramWindowSeconds === b.spectrogramWindowSeconds &&
    sameArray(a.waveform, b.waveform) &&
    a.waveformPeak === b.waveformPeak &&
    a.loudnessIntegrated === b.loudnessIntegrated &&
    a.loudnessShortTerm === b.loudnessShortTerm &&
    a.loudnessRange === b.loudnessRange &&
    a.loudnessTruePeak === b.loudnessTruePeak;
}

function sameDiagnostics(a?: DiagnosticInfo | null, b?: DiagnosticInfo | null): boolean {
  if (a === b) return true;
  if (!a || !b) return false;
  return a.cpuPercent === b.cpuPercent &&
    a.cpuShare === b.cpuShare &&
    a.gpuPercent === b.gpuPercent &&
    a.renderTier === b.renderTier &&
    a.workingSetMb === b.workingSetMb &&
    a.managedMb === b.managedMb &&
    a.threads === b.threads &&
    a.processUptimeSeconds === b.processUptimeSeconds &&
    a.nodeCount === b.nodeCount &&
    a.linkCount === b.linkCount &&
    a.sourceCount === b.sourceCount &&
    a.sourcesRunning === b.sourcesRunning &&
    a.worstBacklogMs === b.worstBacklogMs &&
    a.sourceFillRateMsPerSec === b.sourceFillRateMsPerSec &&
    a.outputRingFillPercent === b.outputRingFillPercent &&
    a.underrunsPerMinute === b.underrunsPerMinute &&
    a.skippedFramesPerMinute === b.skippedFramesPerMinute &&
    a.latencyMeanMs === b.latencyMeanMs &&
    a.latencyStdDevMs === b.latencyStdDevMs &&
    a.estimatedDriftPpm === b.estimatedDriftPpm &&
    a.skippedFrames === b.skippedFrames &&
    a.outputCount === b.outputCount &&
    a.outputsRunning === b.outputsRunning &&
    a.worstLatencyMs === b.worstLatencyMs &&
    a.engineBlockMs === b.engineBlockMs &&
    a.outputTargetMs === b.outputTargetMs &&
    a.outputBufferedMs === b.outputBufferedMs &&
    a.endpointLatencyMs === b.endpointLatencyMs &&
    a.engineQueueMs === b.engineQueueMs &&
    a.chainLatencyMs === b.chainLatencyMs;
}

function sameSnapshot(a: TelemetrySnapshot, b: TelemetrySnapshot): boolean {
  if (a === b) return true;
  if (a.running !== b.running || a.clipping !== b.clipping ||
      a.masterGain !== b.masterGain || a.masterMuted !== b.masterMuted ||
      a.micMuted !== b.micMuted || !sameDiagnostics(a.diagnostics, b.diagnostics)) return false;
  const ids = new Set([...Object.keys(a.meters), ...Object.keys(b.meters)]);
  for (const id of ids) {
    if (!sameNode(a.meters[id], b.meters[id])) return false;
  }
  return true;
}

function sameArray(a?: number[], b?: number[]): boolean {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  for (let index = 0; index < a.length; index++) {
    if (a[index] !== b[index]) return false;
  }
  return true;
}

export function publishTelemetry(next: TelemetrySnapshot): void {
  const previous = snapshot;
  const changed = !sameSnapshot(previous, next);
  snapshot = next;

  const ids = new Set([...Object.keys(previous.meters), ...Object.keys(next.meters)]);
  for (const id of ids) {
    if (!sameNode(previous.meters[id], next.meters[id])) {
      for (const listener of nodeListeners.get(id) ?? []) listener();
    }
  }

  const activeIds = new Set<string>();
  for (const [id, meter] of Object.entries(next.meters)) {
    const threshold = nodeActive.get(id) ? 0.0003 : 0.00075;
    if (meter.level > threshold) activeIds.add(id);
  }

  for (const id of new Set([...nodeActive.keys(), ...activeIds])) {
    const active = activeIds.has(id);
    if (active !== (nodeActive.get(id) ?? false)) {
      nodeActive.set(id, active);
      for (const listener of activeListeners.get(id) ?? []) listener();
    }
  }

  for (const id of nodeActive.keys()) {
    if (!(id in next.meters)) nodeActive.delete(id);
  }

  recomputeTitleLevels();
  if (changed) {
    for (const listener of globalListeners) listener();
  }

  const nextUi = {
    running: next.running,
    micMuted: next.micMuted,
    masterMuted: next.masterMuted,
    masterGain: next.masterGain,
  };
  if (nextUi.running !== uiSnapshot.running ||
      nextUi.micMuted !== uiSnapshot.micMuted ||
      nextUi.masterMuted !== uiSnapshot.masterMuted ||
      nextUi.masterGain !== uiSnapshot.masterGain) {
    uiSnapshot = nextUi;
    for (const listener of uiListeners) listener();
  }

  const activeRecordings = Object.values(next.meters).filter(meter => meter.recordingStatus === 'REC');
  const nextRecording = {
    count: activeRecordings.length,
    seconds: activeRecordings.reduce((longest, meter) => Math.max(longest, meter.recordingSeconds ?? 0), 0),
  };
  if (nextRecording.count !== recordingSnapshot.count || nextRecording.seconds !== recordingSnapshot.seconds) {
    recordingSnapshot = nextRecording;
    for (const listener of recordingListeners) listener();
  }
}

export function setNodeTitles(titles: Record<string, string>): void {
  nodeTitles = titles;
  recomputeTitleLevels();
}

function recomputeTitleLevels(): void {
  if (titleListeners.size === 0) return;
  const next = new Map<string, number>();
  for (const [id, meter] of Object.entries(snapshot.meters)) {
    const title = nodeTitles[id];
    if (title) {
      next.set(title, Math.max(next.get(title) ?? 0, meter.level));
    }
  }

  const titles = new Set([...titleLevels.keys(), ...next.keys()]);
  titleLevels.clear();
  for (const [title, level] of next) titleLevels.set(title, level);
  for (const title of titles) {
    const active = (titleLevels.get(title) ?? 0) > 0.00075;
    if (active !== (titleActive.get(title) ?? false)) {
      titleActive.set(title, active);
      for (const listener of titleListeners.get(title) ?? []) listener();
    }
  }

  for (const title of titleActive.keys()) {
    if (!next.has(title)) titleActive.delete(title);
  }
}

export function useTitleActive(title: string): boolean {
  const subscribe = useCallback((listener: () => void) => {
    let listeners = titleListeners.get(title);
    if (!listeners) {
      listeners = new Set();
      titleListeners.set(title, listeners);
    }
    listeners.add(listener);
    return () => {
      listeners?.delete(listener);
      if (listeners?.size === 0) titleListeners.delete(title);
    };
  }, [title]);
  const getSnapshot = useCallback(() => (titleLevels.get(title) ?? 0) > 0.00075, [title]);
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}

function subscribeGlobal(listener: () => void): () => void {
  globalListeners.add(listener);
  return () => globalListeners.delete(listener);
}

function subscribeUi(listener: () => void): () => void {
  uiListeners.add(listener);
  return () => uiListeners.delete(listener);
}

function subscribeRecording(listener: () => void): () => void {
  recordingListeners.add(listener);
  return () => recordingListeners.delete(listener);
}

export function useGlobalTelemetry(): TelemetrySnapshot {
  return useSyncExternalStore(subscribeGlobal, () => snapshot, () => emptyTelemetry);
}

export function useUiTelemetry() {
  return useSyncExternalStore(subscribeUi, () => uiSnapshot, () => uiSnapshot);
}

export function useRecordingSummary() {
  return useSyncExternalStore(subscribeRecording, () => recordingSnapshot, () => recordingSnapshot);
}

export function getNodeTelemetry(id: string): NodeTelemetry | undefined {
  return snapshot.meters[id];
}

export function useNodeTelemetry(id: string): NodeTelemetry | undefined {
  const subscribe = useCallback((listener: () => void) => {
    let listeners = nodeListeners.get(id);
    if (!listeners) {
      listeners = new Set();
      nodeListeners.set(id, listeners);
    }
    listeners.add(listener);
    return () => {
      listeners?.delete(listener);
      if (listeners?.size === 0) nodeListeners.delete(id);
    };
  }, [id]);
  const getSnapshot = useCallback(() => snapshot.meters[id], [id]);
  return useSyncExternalStore(subscribe, getSnapshot, () => undefined);
}

export function useNodeActive(id: string): boolean {
  const subscribe = useCallback((listener: () => void) => {
    let listeners = activeListeners.get(id);
    if (!listeners) {
      listeners = new Set();
      activeListeners.set(id, listeners);
    }
    listeners.add(listener);
    return () => {
      listeners?.delete(listener);
      if (listeners?.size === 0) activeListeners.delete(id);
    };
  }, [id]);
  const getSnapshot = useCallback(() => nodeActive.get(id) ?? false, [id]);
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}
