import { memo, useEffect, useRef, useState } from 'react';
import { Handle, Position } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import {
  AppWindow, AudioLines, FileAudio, Layers3, Mic, MonitorSpeaker, Power, Radio,
  Settings, SlidersHorizontal, Square, Unlink, Volume2, VolumeX, X,
} from 'lucide-react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useNodeTelemetry } from '../telemetry';
import type { NodeTelemetry } from '../types';
import ParameterControl from './ParameterControl';
import TranscriptionView from './TranscriptionView';
import WaveformView from './WaveformView';

const RECORD_FORMATS = [
  ['Wav24', 'WAV · 24-bit'],
  ['Wav16', 'WAV · 16-bit'],
  ['Wav32Float', 'WAV · 32-bit float'],
  ['Mp3', 'MP3 · 192 kbps'],
  ['Flac', 'FLAC · lossless'],
] as const;

function FlavorIcon({ flavor }: { flavor: string }) {
  const props = { size: 14, strokeWidth: 1.8, 'aria-hidden': true } as const;
  switch (flavor) {
    case 'app': return <AppWindow {...props} />;
    case 'mic': return <Mic {...props} />;
    case 'system': return <AudioLines {...props} />;
    case 'asio': return <Radio {...props} />;
    case 'output': return <MonitorSpeaker {...props} />;
    case 'virtual': return <Radio {...props} />;
    case 'recorder': return <FileAudio {...props} />;
    default: return <SlidersHorizontal {...props} />;
  }
}

function LevelMeter({ level, clipping }: { level: number; clipping: boolean }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const context = canvas.getContext('2d');
    if (!context) return;
    const width = canvas.width;
    const height = canvas.height;
    context.clearRect(0, 0, width, height);
    context.fillStyle = 'rgba(255,255,255,0.065)';
    context.fillRect(0, 0, width, height);
    const normalized = Math.min(1, Math.max(0, Math.pow(level, 0.65)));
    context.fillStyle = clipping || normalized > 0.9 ? '#FF6B6B' : normalized > 0.7 ? '#E0C23A' : '#43E39A';
    context.fillRect(0, 0, Math.round(width * normalized), height);
  }, [level, clipping]);

  return <canvas ref={canvasRef} className="node-meter" width={184} height={5} aria-label={`Signal level ${Math.round(level * 100)}%`} />;
}

function VolumeControl({ id, value, muted }: { id: string; value: number; muted: boolean }) {
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);

  return (
    <div className="node-volume nodrag">
      <input
        aria-label="Node volume"
        type="range"
        min={0}
        max={1.5}
        step={0.01}
        value={local}
        onChange={event => {
          const next = Number(event.target.value);
          setLocal(next);
          send('gain', { id, value: next });
        }}
      />
      <span>{Math.round(local * 100)}%</span>
      <button
        className={`icon-button compact ${muted ? 'danger-text' : ''}`}
        title={muted ? 'Unmute node' : 'Mute node'}
        aria-label={muted ? 'Unmute node' : 'Mute node'}
        onClick={() => send('mute', { id, value: !muted })}
      >
        {muted ? <VolumeX size={14} /> : <Volume2 size={14} />}
      </button>
    </div>
  );
}

function RecorderControls({ node, telemetry }: { node: RuseFlowNode['data']; telemetry?: NodeTelemetry }) {
  const recording = telemetry?.recordingStatus === 'REC';
  const fileName = node.recordingPath ? node.recordingPath.split(/[\\/]/).pop() : 'No file selected';
  return (
    <div className="recorder-controls nodrag">
      <div className="recorder-format">
        <select
          aria-label="Recording format"
          value={node.recordingFormat}
          disabled={recording}
          onChange={event => send('recorder-format', { id: node.id, format: event.target.value })}
        >
          {RECORD_FORMATS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
        <button className="small-button" disabled={recording} onClick={() => send('recorder-file', { id: node.id, format: node.recordingFormat })}>
          {node.recordingPath ? 'Change' : 'Choose file'}
        </button>
      </div>
      <div className="recorder-path" title={node.recordingPath}>{fileName}</div>
      <div className={`recorder-state ${recording ? 'recording' : ''} ${telemetry?.recordingError ? 'error' : ''}`}>
        <span className="status-dot" />
        <span>{telemetry?.recordingStatus ?? (node.recordingPath ? 'ARMED' : 'NO FILE')}</span>
        {recording && <span>{formatTime(telemetry?.recordingSeconds ?? 0)}</span>}
      </div>
      {recording && <button className="recorder-stop nodrag" onClick={() => send('recorder-stop', { id: node.id })}><Square size={11} /> Stop recording</button>}
      {telemetry?.recordingError && <div className="node-error">{telemetry.recordingError}</div>}
    </div>
  );
}

function formatTime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, '0')}`;
}

function RuseNode({ data, selected }: NodeProps<RuseFlowNode>) {
  const telemetry = useNodeTelemetry(data.id);
  const isModifier = data.kind === 'modifier';
  const enabled = data.enabled !== false;
  const recording = telemetry?.recordingStatus === 'REC';
  const installedModels = data.transcriberModels?.filter(model => model.installed) ?? [];
  const selectedModel = installedModels.some(model => model.id === data.transcriberModel)
    ? data.transcriberModel
    : installedModels[0]?.id ?? '';
  const flowing = enabled && (telemetry?.level ?? 0) > 0.01;
  const classes = [
    'ruse-node',
    `node-${data.kind}`,
    selected ? 'selected' : '',
    data.muted && data.highlightMuted ? 'muted' : '',
    telemetry?.clipping ? 'clipping' : '',
    recording ? 'recording' : '',
    flowing ? 'flowing' : '',
    enabled ? '' : 'disabled',
  ].filter(Boolean).join(' ');

  return (
    <div className={classes} style={{ '--accent': data.accent } as React.CSSProperties}>
      {data.kind !== 'source' && <Handle id="in" type="target" position={Position.Left} className="ruse-handle" />}
      <header className="node-header">
        <span className="node-icon"><FlavorIcon flavor={data.flavor} /></span>
        <strong title={data.title}>{data.title}</strong>
        {!enabled && <span className="node-badge bypass-badge">BYPASS</span>}
        {recording && <span className="node-badge rec-badge">REC</span>}
        {telemetry?.clipping && <span className="node-badge clip-badge">CLIP</span>}
        {isModifier && <button className={`node-power nodrag ${enabled ? '' : 'off'}`} title={enabled ? 'Bypass effect' : 'Enable effect'} aria-label={enabled ? 'Bypass effect' : 'Enable effect'} onClick={() => send('effect-enabled', { id: data.id, value: !enabled })}>
          <Power size={13} />
        </button>}
        <button className="node-remove nodrag" title="Remove node" aria-label={`Remove ${data.title}`} onClick={() => send('remove', { id: data.id })}>
          <X size={13} />
        </button>
      </header>
      <div className="node-subtitle">{data.subtitle}</div>
      {data.micHint && <div className="node-hint" title={data.micHint}>Use “{data.micHint}” as the microphone</div>}
      <LevelMeter level={telemetry?.level ?? 0} clipping={telemetry?.clipping ?? false} />
      {isModifier ? (
        <div className="parameter-list">
          {data.isWaveform && <WaveformView samples={telemetry?.waveform} color={data.accent} />}
          {data.isTranscriber && (installedModels.length > 0
            ? <div className="transcriber-model-picker nodrag">
                <span>Model</span>
                <select value={selectedModel} onChange={event => send('transcriber-model', { id: data.id, model: event.target.value })}>
                  {installedModels.map(model => <option key={model.id} value={model.id}>{model.language} · {model.name}</option>)}
                </select>
                <button className="icon-button compact" title="Manage speech models" aria-label="Manage speech models" onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings'))}><Settings size={13} /></button>
              </div>
            : <div className="transcriber-model-empty">
                <span>No speech models installed.</span>
                <button className="small-button" onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings'))}>Open Settings</button>
              </div>)}
          {data.isTranscriber && <TranscriptionView
            status={telemetry?.transcriptionStatus}
            transcript={telemetry?.transcript}
            partial={telemetry?.partialTranscript}
          />}
          {data.flavor === 'chain' && !!(data.chainElements?.length) && <div className="chain-chips">
            {data.chainElements.map((name, index) => <span className="chain-chip" key={`${name}-${index}`}>{name}</span>)}
          </div>}
          {data.parameters.map(parameter => (
            <ParameterControl
              key={`${parameter.processorIndex}-${parameter.parameterIndex}`}
              parameter={parameter}
              value={parameter.value}
              onChange={value => send('param', {
                id: data.id,
                processor: parameter.processorIndex,
                parameter: parameter.parameterIndex,
                value,
              })}
            />
          ))}
          {data.flavor === 'chain' && <button className="disconnect-button nodrag" onClick={() => send('chain-open', { id: data.id })}>
            <Layers3 size={12} /> Edit chain
          </button>}
          <button className="disconnect-button nodrag" onClick={() => send('disconnect-node', { id: data.id })}>
            <Unlink size={12} /> Disconnect routes
          </button>
        </div>
      ) : (
        <>
          <VolumeControl id={data.id} value={data.volume} muted={data.muted} />
          {data.isRecorder && <RecorderControls node={data} telemetry={telemetry} />}
        </>
      )}
      {data.kind !== 'output' && <Handle id="out" type="source" position={Position.Right} className="ruse-handle" />}
    </div>
  );
}

export default memo(RuseNode);
