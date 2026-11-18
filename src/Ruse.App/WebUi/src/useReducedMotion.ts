import { useSyncExternalStore } from 'react';

export type MotionMode = 'system' | 'reduced' | 'full';

const query = typeof window !== 'undefined' && window.matchMedia
  ? window.matchMedia('(prefers-reduced-motion: reduce)')
  : null;
let systemReduced = query?.matches ?? false;
let motionMode: MotionMode = 'system';
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

query?.addEventListener('change', event => {
  systemReduced = event.matches;
  notify();
});

export function setMotionMode(mode: MotionMode): void {
  motionMode = mode;
  if (typeof document !== 'undefined') {
    document.documentElement.dataset.motion = mode;
  }

  notify();
}

function reducedMotion(): boolean {
  return motionMode === 'reduced' || (motionMode === 'system' && systemReduced);
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, reducedMotion, () => false);
}
