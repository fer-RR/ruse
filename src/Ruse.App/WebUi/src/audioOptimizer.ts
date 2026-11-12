import { useEffect, useState } from 'react';
import { send, subscribe } from './bridge';

export interface AudioOptimizerTrial {
  label: string;
  blockMs: number;
  outputBufferMode: string;
  outputBufferManualMs: number;
  engineCushionMs: number;
  maxLatencyMs: number;
  underruns: number;
  shortReadFrames: number;
  skippedFrames: number;
  clean: boolean;
}

export interface AudioOptimizerResult extends AudioOptimizerTrial {
  trials: AudioOptimizerTrial[];
}

export interface AudioOptimizerState {
  status: 'idle' | 'running' | 'done' | 'error';
  progress: number;
  message: string;
  error?: string;
  result?: AudioOptimizerResult;
}

const INITIAL: AudioOptimizerState = { status: 'idle', progress: 0, message: '' };

export function useAudioOptimizer() {
  const [state, setState] = useState<AudioOptimizerState>(INITIAL);

  useEffect(() => {
    const unsubscribe = subscribe((type, payload) => {
      if (type === 'audio-optimizer' && payload && typeof payload === 'object') {
        setState(payload as AudioOptimizerState);
      }
    });
    send('audio-optimizer-status');
    return unsubscribe;
  }, []);

  return {
    state,
    run: () => send('audio-optimizer-run'),
  };
}
