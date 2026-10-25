import type { CatalogSnapshot, GraphSnapshot, SettingsModel, TelemetrySnapshot } from './types';

export const demoCatalog: CatalogSnapshot = {
  apps: [
    { id: 'demo-discord', kind: 'app', name: 'Discord', subtitle: 'Application', details: 'Discord voice and chat', processId: 1234 },
    { id: 'demo-game', kind: 'app', name: 'Game (Counter-Strike)', subtitle: 'Application', details: 'Game audio session', processId: 5678 },
    { id: 'demo-spotify', kind: 'app', name: 'Spotify', subtitle: 'Application', details: 'Music playback', processId: 9012 },
  ],
  outputs: [
    { id: 'demo-headphones', kind: 'output', name: 'Headphones', subtitle: 'WASAPI output', details: 'Primary listening device' },
    { id: 'demo-speakers', kind: 'output', name: 'Studio speakers', subtitle: 'WASAPI output', details: 'Nearfield monitors' },
  ],
  inputs: [
    { id: 'demo-mic', kind: 'input', name: 'Microphone', subtitle: 'Input device', details: 'Studio microphone' },
  ],
  virtuals: [
    { id: 'demo-virtual', kind: 'virtual', name: 'Voicemeeter Input', subtitle: 'Virtual output', details: 'Feed Discord or OBS', micHint: 'Voicemeeter Out B1' },
  ],
  effects: [
    { id: 'gain', kind: 'effect', name: 'Gain', subtitle: 'Level', details: 'Adjust signal level' },
    { id: 'high-pass', kind: 'effect', name: 'High-pass', subtitle: 'Filter', details: 'Remove low-frequency rumble' },
    { id: 'compressor', kind: 'effect', name: 'Compressor', subtitle: 'Dynamics', details: 'Control vocal dynamics' },
    { id: 'eq-bell', kind: 'effect', name: 'EQ bell', subtitle: 'Equalizer', details: 'Shape a frequency band' },
  ],
  chains: [
    { id: 'demo-chain', kind: 'chain', name: 'Broadcast voice', subtitle: 'High-pass → Compressor', details: 'Saved voice processing chain' },
  ],
};

export const demoGraph: GraphSnapshot = {
  version: 1,
  running: true,
  nodes: [
    { id: 'discord', kind: 'source', flavor: 'app', title: 'Discord', subtitle: 'Application', accent: '#5B8DEF', x: 40, y: 60, volume: 1, muted: false, parameters: [], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'game', kind: 'source', flavor: 'app', title: 'Game (Counter-Strike)', subtitle: 'Application', accent: '#5B8DEF', x: 40, y: 330, volume: 0.85, muted: false, parameters: [], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'mic', kind: 'source', flavor: 'mic', title: 'Microphone', subtitle: 'Input device', accent: '#B18CFF', x: 40, y: 620, volume: 1, muted: false, parameters: [], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'hp', kind: 'modifier', flavor: 'effect', title: 'High-pass', subtitle: 'Filter', accent: '#E5AD51', x: 390, y: 270, volume: 1, muted: false, parameters: [{ processorIndex: 0, parameterIndex: 0, name: 'Cutoff', min: 20, max: 1000, value: 80, unit: 'Hz', logarithmic: true, isToggle: false, isInteger: false, isChoice: false, options: [] }], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'comp', kind: 'modifier', flavor: 'effect', title: 'Compressor', subtitle: 'Dynamics', accent: '#E5AD51', x: 680, y: 510, volume: 1, muted: false, parameters: [
      { processorIndex: 0, parameterIndex: 0, name: 'Threshold', min: -60, max: 0, value: -18, unit: 'dB', logarithmic: false, isToggle: false, isInteger: false, isChoice: false, options: [] },
      { processorIndex: 0, parameterIndex: 1, name: 'Ratio', min: 1, max: 20, value: 4, unit: ':1', logarithmic: false, isToggle: false, isInteger: false, isChoice: false, options: [] },
    ], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'headphones', kind: 'output', flavor: 'output', title: 'Headphones', subtitle: 'Output device', accent: '#43E39A', x: 940, y: 80, volume: 1, muted: false, parameters: [], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'virtual', kind: 'output', flavor: 'virtual', title: 'Voicemeeter Input', subtitle: 'Virtual output', accent: '#E5AD51', x: 970, y: 580, volume: 1, muted: false, micHint: 'Voicemeeter Out B1', parameters: [], isRecorder: false, recordingPath: '', recordingFormat: 'Wav24' },
    { id: 'recorder', kind: 'output', flavor: 'recorder', title: 'Session recorder', subtitle: 'File output', accent: '#FF8B72', x: 970, y: 870, volume: 1, muted: false, parameters: [], isRecorder: true, recordingPath: 'C:\\Recordings\\ruse-session.wav', recordingFormat: 'Wav24' },
  ],
  links: [
    { id: 'discord-headphones', from: 'discord', to: 'headphones', kind: 'direct', gain: 1 },
    { id: 'game-headphones', from: 'game', to: 'headphones', kind: 'direct', gain: 0.85 },
    { id: 'mic-hp', from: 'mic', to: 'hp', kind: 'source-modifier', gain: 1 },
    { id: 'hp-comp', from: 'hp', to: 'comp', kind: 'modifier-modifier', gain: 1 },
    { id: 'comp-virtual', from: 'comp', to: 'virtual', kind: 'modifier-output', gain: 1 },
    { id: 'comp-recorder', from: 'comp', to: 'recorder', kind: 'modifier-output', gain: 1 },
    { id: 'discord-virtual', from: 'discord', to: 'virtual', kind: 'direct', gain: 1 },
  ],
};

export const demoSettings: SettingsModel = {
  startRoutingOnLaunch: false,
  highlightMutedNodes: true,
  signalGlow: true,
  highlightCableOnHover: true,
  minimapMode: 2,
  startWithWindows: false,
  transcriberModel: '',
  transcriberModels: [],
};

export function createDemoTelemetry(time: number): TelemetrySnapshot {
  const meters: TelemetrySnapshot['meters'] = {};
  for (const node of demoGraph.nodes) {
    const phase = time / 700 + node.id.length;
    meters[node.id] = {
      level: node.kind === 'modifier' ? 0 : 0.25 + Math.abs(Math.sin(phase)) * 0.45,
      clipping: false,
      recordingStatus: node.isRecorder ? 'REC' : undefined,
      recordingSeconds: node.isRecorder ? Math.floor(time / 1000) : undefined,
    };
  }

  return {
    meters,
    running: true,
    clipping: false,
    masterGain: 1,
    masterMuted: false,
    micMuted: false,
  };
}
