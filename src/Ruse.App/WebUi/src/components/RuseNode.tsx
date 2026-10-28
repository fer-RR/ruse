import { memo, useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Handle, Position } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import {
  AppWindow, AudioLines, FileAudio, Layers3, Mic, MonitorSpeaker, Power, Radio,
  Settings, SlidersHorizontal, Volume2, VolumeX, X,
} from 'lucide-react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';
import { useNodeTelemetry } from '../telemetry';
import type { NodeTelemetry } from '../types';
import DiagnosticsNode from './DiagnosticsNode';
import ParameterControl from './ParameterControl';
import TranscriptionView from './TranscriptionView';
import WaveformView from './WaveformView';

const RECORD_FORMATS = [
  ['Wav24', 'WAV \u00B7 24-bit'],
  ['Wav16', 'WAV \u00B7 16-bit'],
  ['Wav32Float', 'WAV \u00B7 32-bit float'],
  ['Mp3', 'MP3 \u00B7 192 kbps'],
  ['Flac', 'FLAC \u00B7 lossless'],
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
    case 'file': return <FileAudio {...props} />;
    case 'recorder': return <FileAudio {...props} />;
    default: return <SlidersHorizontal {...props} />;
  }
}


function VolumeControl({ id, value, muted }: { id: string; value: number; muted: boolean }) {
  const { t } = useI18n();
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);

  return (
    <div className="node-volume nodrag">
      <input
        aria-label={t('Node volume')}
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
        title={muted ? t('Unmute node') : t('Mute node')}
        aria-label={muted ? t('Unmute node') : t('Mute node')}
        onClick={() => send('mute', { id, value: !muted })}
      >
        {muted ? <VolumeX size={14} /> : <Volume2 size={14} />}
      </button>
    </div>
  );
}

function FilePlayerControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const fileName = node.filePath ? node.filePath.split(/[\\/]/).pop() : t('No file selected');
  return (
    <div className="file-player-controls nodrag">
      <div className="file-player-name" title={node.filePath ?? ''}>{fileName}</div>
      <div className="file-player-actions">
        <button className="small-button" onClick={() => send('file-player-file', { id: node.id })}>{t('Choose file')}</button>
        <button className="small-button" onClick={() => send('file-player-restart', { id: node.id })}>{t('Restart')}</button>
        <label className="file-player-loop">
          <input
            type="checkbox"
            checked={node.fileLoop === true}
            onChange={event => send('file-player-loop', { id: node.id, value: event.target.checked })}
          />
          <span>{t('Loop')}</span>
        </label>
      </div>
    </div>
  );
}

function RecorderControls({ node, telemetry }: { node: RuseFlowNode['data']; telemetry?: NodeTelemetry }) {
  const { t } = useI18n();
  const mode = node.recorderMode ?? 'Off';
  const autoSave = node.recorderAutoSave === true;
  const recording = telemetry?.recordingStatus === 'REC';
  const fileName = node.recordingPath ? node.recordingPath.split(/[\\/]/).pop() : t('No file selected');
  return (
    <div className="recorder-controls nodrag">
      <div className="recorder-mode" role="group" aria-label={t('Recorder mode')}>
        {(['Off', 'Armed', 'On'] as const).map(value => (
          <button
            key={value}
            type="button"
            className={mode === value ? 'active' : ''}
            onClick={() => send('recorder-mode', { id: node.id, mode: value })}
          >{t(value)}</button>
        ))}
      </div>
      <label className="recorder-auto">
        <input
          type="checkbox"
          checked={autoSave}
          disabled={recording}
          onChange={event => send('recorder-autosave', { id: node.id, value: event.target.checked })}
        />
        <span>{t('Auto save (date and time in filename)')}</span>
      </label>
      <div className="recorder-format">
        <select
          aria-label={t('Recording format')}
          value={node.recordingFormat}
          disabled={recording}
          onChange={event => send('recorder-format', { id: node.id, format: event.target.value })}
        >
          {RECORD_FORMATS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
        <button className="small-button" disabled={recording || autoSave} onClick={() => send('recorder-file', { id: node.id, format: node.recordingFormat })}>
          {node.recordingPath ? t('Change') : t('Choose file')}
        </button>
      </div>
      <div className="recorder-path" title={node.recordingPath}>
        {autoSave && !node.recordingPath ? t('Auto save · Music/Ruse Recordings') : fileName}
      </div>
      <div className={`recorder-state ${recording ? 'recording' : ''} ${telemetry?.recordingError ? 'error' : ''}`}>
        <span className="status-dot" />
        <span>{telemetry?.recordingStatus ?? mode.toUpperCase()}</span>
        {recording && <span>{formatTime(telemetry?.recordingSeconds ?? 0)}</span>}
      </div>
      {telemetry?.recordingError && <div className="node-error">{telemetry.recordingError}</div>}
    </div>
  );
}

function formatTime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, '0')}`;
}

function DbMeterView({ db, peakDb, vertical }: { db?: number; peakDb?: number; vertical: boolean }) {
  const value = Number.isFinite(db) ? Math.max(-60, Math.min(0, db!)) : -60;
  const peak = Number.isFinite(peakDb) ? Math.max(-60, Math.min(0, peakDb!)) : -60;
  const label = value <= -59.9 ? '-∞' : value.toFixed(1);
  const fill = (value + 60) / 60 * 100;
  const peakFill = (peak + 60) / 60 * 100;
  if (vertical) {
    return (
      <div className="db-meter-view vertical">
        <div className="db-meter-readout vertical"><strong>{label}</strong><span>dBFS</span></div>
        <div className="db-meter-vertical-body">
          <div className="db-meter-column">
            <span style={{ height: `${fill}%` }} />
            <i style={{ bottom: `${peakFill}%` }} />
          </div>
          <div className="db-meter-scale vertical">
            <span>0</span><span>-6</span><span>-12</span><span>-18</span><span>-24</span>
            <span>-30</span><span>-36</span><span>-42</span><span>-48</span><span>-54</span><span>-60</span>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="db-meter-view horizontal">
      <div className="db-meter-readout"><strong>{label}</strong><span>dBFS</span></div>
      <div className="db-meter-track">
        <span style={{ width: `${fill}%` }} />
        <i style={{ left: `${peakFill}%` }} />
      </div>
      <div className="db-meter-scale"><span>-60</span><span>-48</span><span>-36</span><span>-24</span><span>-12</span><span>0</span></div>
    </div>
  );
}
function RuseNode({ id, data, selected }: NodeProps<RuseFlowNode>) {
  const { t } = useI18n();
  const telemetry = useNodeTelemetry(data.id);
  if (data.diagnostics) {
    return <DiagnosticsNode nodeId={id} />;
  }

  const isModifier = data.kind === 'modifier';
  const isDbMeter = data.isDbMeter === true;
  const dbVertical = isDbMeter && (telemetry?.dbMeterVertical ?? true);
  const enabled = data.enabled !== false;
  const recording = telemetry?.recordingStatus === 'REC';
  const installedModels = data.transcriberModels?.filter(model => model.installed) ?? [];
  const selectedModel = installedModels.some(model => model.id === data.transcriberModel)
    ? data.transcriberModel
    : installedModels[0]?.id ?? '';
  const waveformAmplitude = data.isWaveform
    ? data.parameters.find(parameter => parameter.name === 'Gain')
    : undefined;
  const flowing = enabled && (telemetry?.level ?? 0) > 0.01;
  const classes = [
    'ruse-node',
    `node-${data.kind}`,
    selected ? 'selected' : '',
    data.muted && data.highlightMuted ? 'muted' : '',
    telemetry?.clipping ? 'clipping' : '',
    data.duplicateRisk ? (data.warningSeverity === 'info' ? 'copy' : 'duplicate') : '',
    recording ? 'recording' : '',
    flowing ? 'flowing' : '',
    isDbMeter ? (dbVertical ? 'db-meter-node db-meter-vertical' : 'db-meter-node db-meter-horizontal') : '',
    enabled ? '' : 'disabled',
  ].filter(Boolean).join(' ');

  return (
    <div className={classes} style={{ '--accent': data.accent } as CSSProperties}>
      {data.kind !== 'source' && <Handle id="in" type="target" position={Position.Left} className="ruse-handle" />}
      <header className="node-header">
        <span className="node-icon"><FlavorIcon flavor={data.flavor} /></span>
        <strong
          title={data.title}
          onDoubleClick={event => {
            event.stopPropagation();
            window.dispatchEvent(new CustomEvent('ruse-rename-node', { detail: { id: data.id, title: data.title, chain: data.flavor === 'chain' } }));
          }}
        >{data.title}</strong>
        {!enabled && <span className="node-badge bypass-badge">BYPASS</span>}
        {recording && <span className="node-badge rec-badge">REC</span>}
        {data.duplicateRisk && <span className={`node-badge ${data.warningSeverity === 'info' ? 'copy-badge' : 'warn-badge'}`} title={data.duplicateWarning}>{data.warningSeverity === 'info' ? 'COPY' : 'DUP'}</span>}
        {telemetry?.clipping && <span className="node-badge clip-badge">CLIP</span>}
        {isModifier && <button className={`node-power nodrag ${enabled ? '' : 'off'}`} title={enabled ? t('Bypass effect') : t('Enable effect')} aria-label={enabled ? t('Bypass effect') : t('Enable effect')} onClick={() => send('effect-enabled', { id: data.id, value: !enabled })}>
          <Power size={13} />
        </button>}
        <button className="node-remove nodrag" title={t('Remove node')} aria-label={t('Remove {name}', { name: data.title })} onClick={() => {
          if (data.isRecorder && telemetry?.recordingStatus === 'REC') {
            window.dispatchEvent(new CustomEvent('ruse-remove-recorder', { detail: { id: data.id, title: data.title } }));
          } else {
            send('remove', { id: data.id });
          }
        }}>
          <X size={13} />
        </button>
      </header>
      {!isDbMeter && <div className="node-subtitle">{t(data.subtitle)}</div>}
      {!isDbMeter && data.micHint && <div className="node-hint" title={data.micHint}>{t('Use {name} as the microphone', { name: data.micHint })}</div>}
      {isDbMeter ? (
        <div className="db-meter-body">
          <DbMeterView db={telemetry?.db} peakDb={telemetry?.peakDb} vertical={dbVertical} />
        </div>
      ) : isModifier ? (
        <div className="parameter-list">
          {data.isWaveform && <WaveformView
            samples={telemetry?.waveform}
            color={data.accent}
            amplitude={waveformAmplitude?.value}
            min={waveformAmplitude?.min}
            max={waveformAmplitude?.max}
            onAmplitudeChange={waveformAmplitude
              ? value => send('param', {
                  id: data.id,
                  processor: waveformAmplitude.processorIndex,
                  parameter: waveformAmplitude.parameterIndex,
                  value,
                })
              : undefined}
          />}
          {data.isTranscriber && (installedModels.length > 0
            ? <div className="transcriber-model-picker nodrag">
                <span>{t('Model')}</span>
                <select value={selectedModel} onChange={event => send('transcriber-model', { id: data.id, model: event.target.value })}>
                  {installedModels.map(model => <option key={model.id} value={model.id} title={model.name}>{t(model.language)} / {t(model.tier)}</option>)}
                </select>
                <button className="icon-button compact" title={t('Manage speech models')} aria-label={t('Manage speech models')} onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings', { detail: { tab: 'speech' } }))}><Settings size={13} /></button>
              </div>
            : <div className="transcriber-model-empty">
                <span>{t('No speech models installed.')}</span>
                <button className="small-button" onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings', { detail: { tab: 'speech' } }))}>{t('Open Settings')}</button>
              </div>)}
          {data.isTranscriber && <TranscriptionView
            id={data.id}
            status={telemetry?.transcriptionStatus}
            transcript={telemetry?.transcript}
            partial={telemetry?.partialTranscript}
            error={telemetry?.transcriptionError}
            level={telemetry?.transcriptionLevel}
            hasAudio={telemetry?.transcriptionHasAudio}
            elapsed={telemetry?.transcriptionElapsed}
          />}
          {data.flavor === 'chain' && !!(data.chainElements?.length) && <div className="chain-chips">
            {data.chainElements.map((name, index) => <span className="chain-chip" key={`${name}-${index}`}>{name}</span>)}
          </div>}
          {data.parameters
            .filter(parameter => !(data.isWaveform && parameter.name === 'Gain'))
            .map(parameter => (
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
            <Layers3 size={12} /> {t('Edit chain')}
          </button>}
        </div>
      ) : (
        <>
          {data.isFilePlayer && <FilePlayerControls node={data} />}
          <VolumeControl id={data.id} value={data.volume} muted={data.muted} />
          {data.isRecorder && <RecorderControls node={data} telemetry={telemetry} />}
        </>
      )}
      {data.kind !== 'output' && <Handle id="out" type="source" position={Position.Right} className="ruse-handle" />}
    </div>
  );
}

export default memo(RuseNode);
