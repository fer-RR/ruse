import { useSyncExternalStore } from 'react';
import type { NodeTelemetry, TelemetrySnapshot } from './types';

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

function sameNode(a: NodeTelemetry | undefined, b: NodeTelemetry | undefined): boolean {
  if (a === b) return true;
  if (!a || !b) return false;
  return a.level === b.level &&
    a.clipping === b.clipping &&
    a.filePlaying === b.filePlaying &&
    a.filePaused === b.filePaused &&
    a.filePositionSeconds === b.filePositionSeconds &&
    a.fileDurationSeconds === b.fileDurationSeconds &&
    a.recordingStatus === b.recordingStatus &&
    a.recordingSeconds === b.recordingSeconds &&
    a.recordingError === b.recordingError &&
    a.db === b.db &&
    a.peakDb === b.peakDb &&
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
    sameArray(a.spectrum, b.spectrum) &&
    a.spectrumPeakHz === b.spectrumPeakHz &&
    sameArray(a.spectrogram, b.spectrogram) &&
    a.spectrogramPeakHz === b.spectrogramPeakHz &&
    a.spectrogramColumns === b.spectrogramColumns;
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

  recomputeTitleLevels();
  for (const listener of globalListeners) listener();

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
}

export function useTitleActive(title: string): boolean {
  return useSyncExternalStore(
    listener => {
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
    },
    () => (titleLevels.get(title) ?? 0) > 0.00075,
    () => false,
  );
}

function subscribeGlobal(listener: () => void): () => void {
  globalListeners.add(listener);
  return () => globalListeners.delete(listener);
}

export function useGlobalTelemetry(): TelemetrySnapshot {
  return useSyncExternalStore(subscribeGlobal, () => snapshot, () => emptyTelemetry);
}

export function useUiTelemetry() {
  return useSyncExternalStore(
    listener => {
      uiListeners.add(listener);
      return () => uiListeners.delete(listener);
    },
    () => uiSnapshot,
    () => uiSnapshot,
  );
}

export function useRecordingSummary() {
  return useSyncExternalStore(
    listener => {
      recordingListeners.add(listener);
      return () => recordingListeners.delete(listener);
    },
    () => recordingSnapshot,
    () => recordingSnapshot,
  );
}

export function getNodeTelemetry(id: string): NodeTelemetry | undefined {
  return snapshot.meters[id];
}

export function useNodeTelemetry(id: string): NodeTelemetry | undefined {
  return useSyncExternalStore(
    listener => {
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
    },
    () => snapshot.meters[id],
    () => undefined,
  );
}

export function useNodeActive(id: string): boolean {
  return useSyncExternalStore(
    listener => {
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
    },
    () => nodeActive.get(id) ?? false,
    () => false,
  );
}
