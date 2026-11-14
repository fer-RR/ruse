import { useSyncExternalStore } from 'react';

const query = typeof window !== 'undefined' && window.matchMedia
  ? window.matchMedia('(prefers-reduced-motion: reduce)')
  : null;
let reduced = query?.matches ?? false;
const listeners = new Set<() => void>();

query?.addEventListener('change', event => {
  reduced = event.matches;
  for (const listener of listeners) listener();
});

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, () => reduced, () => false);
}
