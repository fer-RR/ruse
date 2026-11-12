import { useEffect, useState } from 'react';
import { send, subscribe } from './bridge';

export interface UpdateStatus {
  status: 'idle' | 'checking' | 'current' | 'available' | 'downloading' | 'ready' | 'applying' | 'error';
  currentVersion: string;
  latestVersion?: string;
  name?: string;
  notes?: string;
  releaseUrl?: string;
  error?: string;
  progress?: number;
  downloadedBytes?: number;
  totalBytes?: number;
  canApply?: boolean;
}

const INITIAL: UpdateStatus = { status: 'idle', currentVersion: '' };

export function formatBytes(bytes?: number): string {
  if (!bytes || bytes <= 0) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${value >= 10 || unit === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

export function useUpdates() {
  const [state, setState] = useState<UpdateStatus>(INITIAL);

  useEffect(() => {
    const unsubscribe = subscribe((type, payload) => {
      if (type === 'update-status' && payload && typeof payload === 'object') {
        setState(payload as UpdateStatus);
      }
    });
    send('update-status');
    return unsubscribe;
  }, []);

  return {
    state,
    check: () => send('check-updates'),
    download: () => send('download-update'),
    apply: () => send('apply-update'),
  };
}
