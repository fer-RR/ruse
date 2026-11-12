import { memo, useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Handle, Position, useUpdateNodeInternals } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import {
  AppWindow, AudioLines, Check, ChevronDown, ChevronUp, Copy, FileAudio, Languages, Layers3, Mic, MonitorSpeaker,
  Play, Power, Puzzle, Radio, Settings, SlidersHorizontal, Square, Volume2, VolumeX, X,
} from 'lucide-react';
import { send } from '../bridge';
import { formatGainDb, nextVolumeValue } from '../gain';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';
import { withShortcut } from '../shortcuts';
import { TRANSLATION_TARGETS, translationTarget } from '../languages';
import { useNodeActive, useNodeTelemetry } from '../telemetry';
import type { NodeTelemetry } from '../types';
import DiagnosticsNode from './DiagnosticsNode';
import IrControls from './IrControls';
import NoiseProfileControls from './NoiseProfileControls';
import NumberField from './NumberField';
import LoudnessView from './LoudnessView';
import ParameterControl from './ParameterControl';
import ParametricEqView from './ParametricEqView';
import ToneControls from './ToneControls';
import SynthControls from './SynthControls';
import TranscriptionView from './TranscriptionView';
import TranslationView from './TranslationView';
import SpectrumView from './SpectrumView';
import SpectrogramView from './SpectrogramView';
import WaveformView from './WaveformView';
import GraphicEqualizerView from './GraphicEqualizerView';

const RECORD_FORMATS = [
  ['Wav24', 'WAV \u00B7 24-bit'],
  ['Wav16', 'WAV \u00B7 16-bit'],
  ['Wav32Float', 'WAV \u00B7 32-bit float'],
  ['Mp3', 'MP3 \u00B7 192 kbps'],
  ['Flac', 'FLAC \u00B7 lossless'],
] as const;
const RECORD_RATES = [44100, 48000, 96000] as const;

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
    case 'tone': return <AudioLines {...props} />;
    case 'synth': return <SlidersHorizontal {...props} />;
    case 'tts': return <AudioLines {...props} />;
    case 'network': return <Radio {...props} />;
    case 'timeline': return <Play {...props} />;
    case 'recorder': return <FileAudio {...props} />;
    case 'translator': return <Languages {...props} />;
    case 'vst3': return <Puzzle {...props} />;
    case 'analysis': return <AudioLines {...props} />;
    default: return <SlidersHorizontal {...props} />;
  }
}


function VolumeControl({ id, value, muted }: { id: string; value: number; muted: boolean }) {
  const { t } = useI18n();
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  const commit = (next: number) => {
    setLocal(next);
    send('gain', { id, value: next });
  };

  return (
    <div className="node-volume nodrag">
      <span className="volume-slider">
        <input
          aria-label={t('Node volume')}
          aria-valuetext={formatGainDb(local)}
          type="range"
          min={0}
          max={1.5}
          step={0.01}
          value={local}
          onChange={event => commit(Number(event.target.value))}
          onDoubleClick={() => commit(1)}
          onKeyDown={event => {
            const next = nextVolumeValue(event, local);
            if (next !== null) {
              event.preventDefault();
              commit(next);
            }
          }}
        />
        <small className="volume-db">{formatGainDb(local)}</small>
      </span>
      <span>{Math.round(local * 100)}%</span>
      <button
        className={`icon-button compact ${muted ? 'danger-text' : ''}`}
        title={withShortcut(muted ? t('Unmute node') : t('Mute node'), 'muteSelected')}
        aria-label={muted ? t('Unmute node') : t('Mute node')}
        onClick={() => send('mute', { id, value: !muted })}
      >
        {muted ? <VolumeX size={14} /> : <Volume2 size={14} />}
      </button>
    </div>
  );
}

function FilePlayerControls({ node, telemetry }: { node: RuseFlowNode['data']; telemetry?: NodeTelemetry }) {
  const { t } = useI18n();
  const [seekValue, setSeekValue] = useState<number | null>(null);
  const fileName = node.filePath ? node.filePath.split(/[\\/]/).pop() : t('No file selected');
  const hasFile = Boolean(node.filePath);
  const playing = telemetry?.filePlaying === true;
  const paused = telemetry?.filePaused === true;
  const position = telemetry?.filePositionSeconds ?? 0;
  const duration = telemetry?.fileDurationSeconds ?? 0;
  const sliderValue = seekValue ?? (duration > 0 ? Math.round((position / duration) * 1000) : 0);
  const canSeek = hasFile && duration > 0 && (playing || paused);

  const commitSeek = () => {
    if (seekValue === null || duration <= 0) return;
    send('file-player-seek', { id: node.id, seconds: (duration * seekValue) / 1000 });
    setSeekValue(null);
  };

  return (
    <div className="file-player-controls nodrag">
      <div
        className="file-player-name"
        title={node.filePath ?? ''}
        draggable={hasFile}
        onDragStart={event => {
          if (!node.filePath) return;
          event.stopPropagation();
          event.dataTransfer.setData('application/ruse-recording', node.filePath);
          event.dataTransfer.effectAllowed = 'copy';
        }}
      >{fileName}</div>
      <div className="file-player-position">
        <span>{formatTime(position)}</span>
        <input
          type="range"
          min={0}
          max={1000}
          value={sliderValue}
          disabled={!canSeek}
          onChange={event => setSeekValue(Number(event.target.value))}
          onPointerUp={commitSeek}
          onPointerCancel={commitSeek}
          onKeyUp={commitSeek}
          aria-label={t('Position')}
        />
        <span>{formatTime(duration)}</span>
      </div>
      {node.fileOutputs && node.fileOutputs.length > 0 && (
        <label className="file-player-output">
          <span>{t('Output')}</span>
          <select
            value={node.fileOutputId ?? ''}
            onChange={event => send('file-player-output', { id: node.id, outputId: event.target.value })}
          >
            <option value="">{t('None')}</option>
            {node.fileOutputs.map(output => (
              <option key={output.id} value={output.id}>{output.name}</option>
            ))}
          </select>
        </label>
      )}
      <div className="file-player-actions">
        {playing || paused ? (
          <button className="small-button" onClick={() => send(playing ? 'file-player-pause' : 'file-player-resume', { id: node.id })}>
            {playing ? t('Pause') : t('Resume')}
          </button>
        ) : (
          <button className="small-button" disabled={!hasFile} onClick={() => send('file-player-start', { id: node.id })}>
            <Play size={11} /> {t('Start')}
          </button>
        )}
        <button className="small-button" disabled={!playing && !paused} onClick={() => send('file-player-stop', { id: node.id })}>
          <Square size={11} /> {t('Stop')}
        </button>
        <button className="small-button" onClick={() => send('file-player-file', { id: node.id })}>{t('Choose file')}</button>
        <button className="small-button" disabled={!hasFile} onClick={() => send('file-player-restart', { id: node.id })}>{t('Restart')}</button>
        <label className="file-player-speed">
          <span>{t('Speed')}</span>
          <select value={node.fileSpeed ?? 1} onChange={event => send('file-player-speed', { id: node.id, value: Number(event.target.value) })}>
            {[0.5, 0.75, 1, 1.25, 1.5, 2].map(speed => <option key={speed} value={speed}>{speed}x</option>)}
          </select>
        </label>
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

function NetworkAddress({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [copied, setCopied] = useState(false);
  const ip = node.networkLocalIp ?? '';
  const copy = async () => {
    if (!ip) return;
    try {
      if (navigator.clipboard) {
        await navigator.clipboard.writeText(ip);
      } else {
        const area = document.createElement('textarea');
        area.value = ip;
        document.body.appendChild(area);
        area.select();
        document.execCommand('copy');
        area.remove();
      }
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
    }
  };

  return (
    <div className="network-ip">
      <span>{t('Your LAN IP')}: {ip || '—'}</span>
      <button className="icon-button compact" title={copied ? t('Copied') : t('Copy')} aria-label={copied ? t('Copied') : t('Copy')} onClick={copy}>
        {copied ? <Check size={12} /> : <Copy size={12} />}
      </button>
    </div>
  );
}
function NetworkInputControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  return (
    <div className="network-controls nodrag">
      <label className="network-row">
        <span>{t('Listen IP')}</span>
        <input value="0.0.0.0" readOnly />
      </label>
      <label className="network-row">
        <span>{t('UDP port')}</span>
        <NumberField
          value={node.networkPort ?? 7890}
          min={1}
          max={65535}
          step={1}
          onChange={value => send('network-input-port', { id: node.id, value })}
          ariaLabel={t('UDP port')}
        />
      </label>
      {node.muted
        ? <div className="network-status">{t('Disconnected')}</div>
        : node.networkError
        ? <div className="network-error">{node.networkError}</div>
        : <div className="network-status">{t('Listening on UDP')} {node.networkPort ?? 7890} · {Math.round(node.networkBufferedMs ?? 0)} ms</div>}
      <NetworkAddress node={node} />
      <div className="network-actions">
        <button className="small-button" onClick={() => send('mute', { id: node.id, value: !node.muted })}>{node.muted ? t('Connect') : t('Disconnect')}</button>
      </div>
    </div>
  );
}

function NetworkOutputControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [host, setHost] = useState(node.networkHost ?? '127.0.0.1');
  useEffect(() => setHost(node.networkHost ?? '127.0.0.1'), [node.networkHost]);
  const commitHost = () => {
    if (host.trim()) send('network-output-host', { id: node.id, value: host.trim() });
  };
  return (
    <div className="network-controls nodrag">
      <label className="network-row">
        <span>{t('Destination IP')}</span>
        <input value={host} onChange={event => setHost(event.target.value)} onBlur={commitHost} />
      </label>
      <label className="network-row">
        <span>{t('UDP port')}</span>
        <NumberField
          value={node.networkPort ?? 7890}
          min={1}
          max={65535}
          step={1}
          onChange={value => send('network-output-port', { id: node.id, value })}
          ariaLabel={t('UDP port')}
        />
      </label>
      {node.muted
        ? <div className="network-status">{t('Disconnected')}</div>
        : node.networkError
          ? <div className="network-error">{node.networkError}</div>
          : <div className="network-status">{t('Sending UDP PCM')}</div>}
      <NetworkAddress node={node} />
      <div className="network-actions">
        <button className="small-button" onClick={() => send('mute', { id: node.id, value: !node.muted })}>{node.muted ? t('Connect') : t('Disconnect')}</button>
      </div>
    </div>
  );
}
function NoteDetectorView({ telemetry }: { telemetry?: NodeTelemetry }) {
  const active = telemetry?.noteActive === true && telemetry.noteName;
  const cents = telemetry?.noteCents ?? 0;
  return (
    <div className={`note-detector-view ${active ? 'active' : ''}`}>
      <strong>{active ? telemetry?.noteName : '--'}</strong>
      <span>{active ? `${cents >= 0 ? '+' : ''}${cents.toFixed(1)} cents` : 'No stable note'}</span>
      <span>{active ? `${(telemetry?.noteHz ?? 0).toFixed(1)} Hz` : ''}</span>
      <small>{active ? `${Math.round((telemetry?.noteConfidence ?? 0) * 100)}% confidence` : '\u00A0'}</small>
    </div>
  );
}
function TtsControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [text, setText] = useState(node.ttsText ?? '');
  useEffect(() => setText(node.ttsText ?? ''), [node.ttsText]);
  const installed = node.ttsModelInstalled === true;
  const sources = node.ttsSources ?? [];
  const voices = node.ttsVoices ?? [];
  const commitText = () => {
    if (text !== (node.ttsText ?? '')) {
      send('tts-text', { id: node.id, text });
    }
  };

  return (
    <div className="tts-controls nodrag">
      {!installed && (
        <div className="tts-model-empty">
          <span>{t('No TTS model installed.')}</span>
          <button
            className="small-button"
            onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings', { detail: { tab: 'speech' } }))}
          >
            {t('Open Settings')}
          </button>
        </div>
      )}
      <textarea
        className="tts-text"
        value={text}
        placeholder={t('Type text to speak')}
        onChange={event => setText(event.target.value)}
        onBlur={commitText}
      />
      <div className="tts-actions">
        <button className="small-button" onClick={() => send('tts-open-text', { id: node.id })}>{t('Open text file')}</button>
        <button
          className="small-button"
          disabled={!installed || !text.trim()}
          onClick={() => {
            commitText();
            send('tts-speak', { id: node.id });
          }}
        >
          <Play size={11} /> {t('Speak')}
        </button>
        <button className="small-button" onClick={() => send('tts-stop', { id: node.id })}>
          <Square size={11} /> {t('Stop')}
        </button>
      </div>
      <label className="tts-row">
        <span>{t('Read from')}</span>
        <select
          value={node.ttsSourceNodeId ?? ''}
          onChange={event => send('tts-source', { id: node.id, source: event.target.value })}
        >
          <option value="">{t('Manual text')}</option>
          {sources.map(source => (
            <option key={source.id} value={source.id}>{source.title}</option>
          ))}
        </select>
      </label>
      {voices.length > 0 && (
        <label className="tts-row">
          <span>{t('Voice')}</span>
          <select value={node.ttsVoice ?? 0} onChange={event => send('tts-voice', { id: node.id, value: Number(event.target.value) })}>
            {voices.map((voice, index) => <option key={voice} value={index}>{voice}</option>)}
          </select>
        </label>
      )}
      <label className="tts-row">
        <span>{t('Speed')}</span>
        <input
          type="range"
          min={0.5}
          max={2}
          step={0.05}
          value={node.ttsSpeed ?? 1}
          onChange={event => send('tts-speed', { id: node.id, value: Number(event.target.value) })}
        />
        <span>{(node.ttsSpeed ?? 1).toFixed(2)}x</span>
      </label>
      {node.ttsStatus && <div className="tts-status">{t(node.ttsStatus)}</div>}
    </div>
  );
}
function Vst3Controls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const status = node.vst3Status ?? 'Loading plugin';
  const tone = status === 'Error' ? 'error' : status === 'Ready' ? 'ready' : 'loading';
  return (
    <div className="vst3-controls nodrag">
      <div className={`vst3-status ${tone}`}>
        <span className="status-dot" />
        <span>{t(status)}</span>
        {node.vst3LatencyMs ? <span className="vst3-latency">{node.vst3LatencyMs} ms</span> : null}
      </div>
      {(node.vst3Vendor || node.vst3Version) && (
        <div className="vst3-meta">
          {node.vst3Vendor}{node.vst3Version ? ` · ${node.vst3Version}` : ''}
        </div>
      )}
      <div className="vst3-actions">
        <button
          className="small-button"
          disabled={!node.vst3HasEditor || status !== 'Ready'}
          onClick={() => send('vst3-editor', { id: node.id, open: !node.vst3EditorOpen })}
        >
          {node.vst3EditorOpen ? t('Close editor') : t('Open editor')}
        </button>
      </div>
      {node.vst3Error && <div className="node-error">{node.vst3Error}</div>}
    </div>
  );
}

function TranslatorControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t, language } = useI18n();
  const target = translationTarget(node.translatorTarget);
  const opus = node.translatorProvider === 'opus';
  const installed = node.translatorModels?.filter(model => model.installed) ?? [];
  const selected = installed.some(model => model.id === node.translatorModel)
    ? node.translatorModel
    : installed[0]?.id ?? '';
  return (
    <div className="translator-controls nodrag">
      <label className="translator-field">
        <span>{t('Translate to')}</span>
        <select
          value={target.code}
          onChange={event => send('translator-target', { id: node.id, target: event.target.value })}
        >
          <option value={language}>{t('Auto (my language)')}</option>
          {TRANSLATION_TARGETS.map(entry => <option key={entry.code} value={entry.code}>{entry.label}</option>)}
        </select>
      </label>
      {target.needsModel && !opus && (installed.length > 0
        ? <label className="translator-field">
            <span>{t('Translation model')}</span>
            <select
              value={selected}
              onChange={event => send('translator-model', { id: node.id, model: event.target.value })}
            >
              {installed.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
            </select>
          </label>
        : <div className="translator-missing">
            <span>{t('No translation models installed.')}</span>
            <button className="small-button" onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings', { detail: { tab: 'speech' } }))}>
              {t('Open Settings')}
            </button>
          </div>)}
      <div className="translator-hint">
        <span>{opus
          ? t('OPUS-MT uses one local ONNX model per language pair. No audio leaves your PC.')
          : target.needsModel
            ? t('Whisper listens, then the local model translates. No audio leaves your PC.')
            : t('Whisper translates speech directly into English.')}</span>
        <button className="icon-button compact" title={t('Manage translation models')} aria-label={t('Manage translation models')} onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-settings', { detail: { tab: 'speech' } }))}><Settings size={13} /></button>
      </div>
    </div>
  );
}

function RecorderControls({ node, telemetry }: { node: RuseFlowNode['data']; telemetry?: NodeTelemetry }) {
  const { t } = useI18n();
  const mode = node.recorderMode ?? 'Off';
  const autoSave = node.recorderAutoSave === true;
  const saveToSamples = node.recorderSaveToSamples === true;
  const recording = telemetry?.recordingStatus === 'REC';
  const armed = mode === 'Armed' && !recording;
  const fileName = node.recordingPath ? node.recordingPath.split(/[\\/]/).pop() : t('No file selected');
  const silenceSeconds = node.recorderSilenceStopSeconds ?? 0;
  const startThresholdDb = node.recorderStartThresholdDb ?? -40;
  const recordRates: number[] = node.recordingFormat === 'Mp3' ? [44100, 48000] : [...RECORD_RATES];
  const recordRate = recordRates.includes(node.recorderSampleRate ?? 48000)
    ? node.recorderSampleRate ?? 48000
    : 48000;
  const [advanced, setAdvanced] = useState(false);
  const updateNodeInternals = useUpdateNodeInternals();

  useEffect(() => {
    updateNodeInternals(node.id);
  }, [advanced, node.id, updateNodeInternals]);

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
      <label className="recorder-save-location">
        <span>{t('Save auto recordings to')}</span>
        <select
          aria-label={t('Save auto recordings to')}
          value={saveToSamples ? 'samples' : 'recording'}
          disabled={recording}
          onChange={event => send('recorder-save-location', { id: node.id, value: event.target.value === 'samples' })}
        >
          <option value="recording">{t('Ruse Recordings')}</option>
          <option value="samples">{t('Ruse Samples')}</option>
        </select>
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
      <label className="recorder-rate">
        <span>{t('Sample rate')}</span>
        <select
          aria-label={t('Sample rate')}
          value={recordRate}
          disabled={recording}
          onChange={event => send('recorder-rate', { id: node.id, value: Number(event.target.value) })}
        >
          {recordRates.map(rate => <option key={rate} value={rate}>{(rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1)} kHz</option>)}
        </select>
      </label>
      {node.recordingPath ? (
        <button
          type="button"
          className="recorder-path link nodrag"
          title={t('Open destination folder')}
          draggable
          onDragStart={event => {
            if (!node.recordingPath) return;
            event.stopPropagation();
            event.dataTransfer.setData('application/ruse-recording', node.recordingPath);
            event.dataTransfer.effectAllowed = 'copy';
          }}
          onClick={() => send('recorder-open-folder', { id: node.id })}
        >
          {fileName}
        </button>
      ) : (
        <div className="recorder-path" title={node.recordingPath}>
          {autoSave ? (saveToSamples ? t('Auto save to Ruse Samples') : t('Auto save to Ruse Recordings')) : fileName}
        </div>
      )}
      <div className={`recorder-state ${recording ? 'recording' : ''} ${armed ? 'armed' : ''} ${telemetry?.recordingError ? 'error' : ''}`}>
        <span className="status-dot" />
        <span>{telemetry?.recordingStatus ?? mode.toUpperCase()}</span>
        {recording && <span>{formatTime(telemetry?.recordingSeconds ?? 0)}</span>}
      </div>
      {telemetry?.recordingError && <div className="node-error">{telemetry.recordingError}</div>}
      <button
        type="button"
        className="recorder-advanced-toggle"
        aria-expanded={advanced}
        onClick={() => setAdvanced(value => !value)}
      >
        {advanced ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
        <span>{t('Advanced')}</span>
      </button>
      {advanced && (
        <div className="recorder-advanced">
          <label className="recorder-advanced-row">
            <span>{t('Stop after silence')}</span>
            <span className="recorder-advanced-value">
              <NumberField
                value={silenceSeconds}
                min={0}
                max={3600}
                step={1}
                onChange={value => send('recorder-silence', { id: node.id, value })}
                ariaLabel={t('Stop after silence')}
              />
              <span>{t('seconds')}</span>
            </span>
          </label>
          <label className="recorder-advanced-row">
            <span>{t('Start threshold')}</span>
            <span className="recorder-advanced-value">
              <NumberField
                value={startThresholdDb}
                min={-80}
                max={0}
                step={1}
                onChange={value => send('recorder-threshold', { id: node.id, value })}
                ariaLabel={t('Start threshold')}
              />
              <span>dB</span>
            </span>
          </label>
          <div className="recorder-advanced-hint">{t('While armed, start above this level')}</div>
        </div>
      )}
    </div>
  );
}

function formatTime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, '0')}`;
}

function DbMeterView({ db, peakDb, dbLeft, dbRight, peakDbLeft, peakDbRight, vertical }: {
  db?: number;
  peakDb?: number;
  dbLeft?: number;
  dbRight?: number;
  peakDbLeft?: number;
  peakDbRight?: number;
  vertical: boolean;
}) {
  const clampDb = (value?: number) => Number.isFinite(value) ? Math.max(-60, Math.min(0, value!)) : -60;
  const leftValue = clampDb(dbLeft ?? db);
  const rightValue = clampDb(dbRight ?? db);
  const leftPeak = clampDb(peakDbLeft ?? peakDb);
  const rightPeak = clampDb(peakDbRight ?? peakDb);
  const stereo = dbLeft != null || dbRight != null;
  const channels = stereo
    ? [{ label: 'L', value: leftValue, peak: leftPeak }, { label: 'R', value: rightValue, peak: rightPeak }]
    : [{ label: '', value: leftValue, peak: leftPeak }];
  const label = (value: number) => value <= -59.9 ? '-\u221E' : value.toFixed(1);
  const fill = (value: number) => value <= -59.9 ? 0 : (value + 60) / 60 * 100;
  const peakFill = (value: number) => value <= -59.9 ? 0 : (value + 60) / 60 * 100;

  if (vertical) {
    return (
      <div className="db-meter-view vertical">
        <div className={`db-meter-readout vertical ${stereo ? 'stereo' : ''}`}>
          {stereo
            ? <><span>L</span><strong>{label(leftValue)}</strong><span>R</span><strong>{label(rightValue)}</strong></>
            : <><strong>{label(leftValue)}</strong><span>dBFS</span></>}
        </div>
        <div className={`db-meter-vertical-body ${stereo ? 'stereo' : ''}`}>
          {channels.map(channel => (
            <div className="db-meter-channel" key={channel.label || 'mono'}>
              <div className="db-meter-column">
                <span style={{ height: `${fill(channel.value)}%` }} />
                <i style={{ bottom: `${peakFill(channel.peak)}%` }} />
              </div>
              {channel.label && <small>{channel.label}</small>}
            </div>
          ))}
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
      <div className="db-meter-readout">
        <strong>{label(stereo ? Math.max(leftValue, rightValue) : leftValue)}</strong><span>dBFS</span>
      </div>
      {channels.map(channel => (
        <div className="db-meter-channel-row" key={channel.label || 'mono'}>
          {channel.label && <small>{channel.label}</small>}
          <div className="db-meter-track">
            <span style={{ width: `${fill(channel.value)}%` }} />
            <i style={{ left: `${peakFill(channel.peak)}%` }} />
          </div>
        </div>
      ))}
      <div className="db-meter-scale"><span>-60</span><span>-48</span><span>-36</span><span>-24</span><span>-12</span><span>0</span></div>
    </div>
  );
}function formatRate(rate: number): string {
  if (!Number.isFinite(rate) || rate <= 0) return '';
  return rate >= 1000
    ? `${(rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1)}k`
    : `${Math.round(rate)}`;
}

function RuseNode({ id, data, selected }: NodeProps<RuseFlowNode>) {
  const { t } = useI18n();
  const telemetry = useNodeTelemetry(data.id);
  const active = useNodeActive(data.id);
  if (data.diagnostics) {
    return <DiagnosticsNode nodeId={id} />;
  }

  const isModifier = data.kind === 'modifier';
  const isDbMeter = data.isDbMeter === true;
  const isLoudness = data.isLoudness === true;
  const isSpectrum = data.isSpectrum === true;
  const isSpectrogram = data.isSpectrogram === true;
  const isWaveform = data.isWaveform === true;
  const isParametricEq = data.isParametricEq === true;
  const canParallel = isModifier && !isDbMeter && !isLoudness && !isSpectrum && !isSpectrogram && !isWaveform &&
    !data.isTranscriber && !data.isTranslator && !data.isVst3 && !data.isGraphicEq;
  const dbVertical = isDbMeter && (telemetry?.dbMeterVertical ?? true);
  const enabled = data.enabled !== false;
  const recording = telemetry?.recordingStatus === 'REC';
  const installedModels = data.transcriberModels?.filter(model => model.installed) ?? [];
  const selectedModel = installedModels.some(model => model.id === data.transcriberModel)
    ? data.transcriberModel
    : installedModels[0]?.id ?? '';
  const spectrumFloor = data.parameters.find(parameter => parameter.name === 'Floor')?.value ?? -100;
  const spectrumCeiling = data.parameters.find(parameter => parameter.name === 'Ceiling')?.value ?? -30;
  const spectrogramContrast = data.parameters.find(parameter => parameter.name === 'Contrast')?.value ?? 1;
  const spectrogramPalette = Math.round(data.parameters.find(parameter => parameter.name === 'Palette')?.value ?? 0);
  const formatRateValue = data.sampleRate && data.sampleRate > 0 ? data.sampleRate : 48000;
  const formatBits = data.bitsPerSample ?? 32;
  const flowing = enabled && active;
  const classes = [
    'ruse-node',

    `node-${data.kind}`,
    selected ? 'selected' : '',
    data.muted && data.highlightMuted ? 'muted' : '',
    telemetry?.clipping ? 'clipping' : '',
    telemetry?.clipGuardActive ? 'guarded' : '',
    data.duplicateRisk ? (data.warningSeverity === 'info' ? 'copy' : 'duplicate') : '',
    recording ? 'recording' : '',
    flowing ? 'flowing' : '',
    data.isTranscriber ? 'transcriber-node' : '',
    data.isTranslator ? 'translator-node' : '',
    data.isVst3 ? 'vst3-node' : '',
    data.isFilePlayer ? 'file-player-node' : '',
    data.isToneGenerator ? 'tone-node' : '',
    data.isSynthGenerator ? 'synth-node' : '',
    data.isNoteDetector ? 'note-detector-node' : '',
    data.isTts ? 'tts-node' : '',
    data.isRecorder ? 'recorder-node' : '',
    isSpectrum ? 'spectrum-node' : '',
    isSpectrogram ? 'spectrogram-node' : '',
    isWaveform ? 'waveform-node' : '',
    data.isGraphicEq ? 'graphic-eq-node' : '',
    isParametricEq ? 'parametric-eq-node' : '',
    data.isConvolutionReverb ? 'convolution-node' : '',
    isDbMeter ? (dbVertical ? 'db-meter-node db-meter-vertical' : 'db-meter-node db-meter-horizontal') : '',
    isLoudness ? 'loudness-node' : '',
    enabled ? '' : 'disabled',
  ].filter(Boolean).join(' ');

  return (
    <div className={classes} style={{ '--accent': data.accent } as CSSProperties} role="group" aria-label={`${data.title}. ${t(data.subtitle)}`}>
      {data.kind !== 'source' && <Handle id="in" type="target" position={Position.Left} className={`ruse-handle ${flowing ? 'active' : ''}`} />}
      {data.supportsSidechain && <Handle id="sidechain" type="target" position={Position.Left} className="ruse-handle sidechain-handle" style={{ top: '72%' }} />}
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
        {telemetry?.clipGuardActive && <span className="node-badge guard-badge" title={t('Clip guard active')}>GUARD</span>}
        {data.isTts && data.ttsUsesGpu !== true && <span className="node-badge cpu-badge">CPU</span>}
        {(data.isTranscriber || data.isTranslator) && typeof telemetry?.usesGpu === 'boolean' && (
          <span className={`node-badge ${telemetry.usesGpu ? 'gpu-badge' : 'cpu-badge'}`}>{telemetry.usesGpu ? 'GPU' : 'CPU'}</span>
        )}
        {data.showDiagnostics && (data.kind === 'source' || data.kind === 'output') ? (
          <span
            className="node-badge format-badge"
            title={`${(formatRateValue / 1000).toFixed(1)} kHz / ${formatBits}-bit`}
          >
            {formatRate(formatRateValue)} · {formatBits}-bit
          </span>
        ) : null}
        {isModifier && <button className={`node-power nodrag ${enabled ? '' : 'off'}`} title={withShortcut(enabled ? t('Bypass effect') : t('Enable effect'), 'bypass')} aria-label={enabled ? t('Bypass effect') : t('Enable effect')} onClick={() => send('effect-enabled', { id: data.id, value: !enabled })}>
          <Power size={13} />
        </button>}
        <button className="node-remove nodrag" title={withShortcut(t('Remove node'), 'remove')} aria-label={t('Remove {name}', { name: data.title })} onClick={() => {
          if (data.isRecorder && telemetry?.recordingStatus === 'REC') {
            window.dispatchEvent(new CustomEvent('ruse-remove-recorder', { detail: { id: data.id, title: data.title } }));
          } else {
            send('remove', { id: data.id });
          }
        }}>
          <X size={13} />
        </button>
      </header>
      {!isDbMeter && !isLoudness && <div className="node-subtitle">{t(data.subtitle)}</div>}
      {!isDbMeter && !isLoudness && data.micHint && <div className="node-hint" title={data.micHint}>{t('Use {name} as the microphone', { name: data.micHint })}</div>}
      {data.showDiagnostics && data.controlId && (
        <div className="node-control-id nodrag" title={`${t('Control ID')}: ${data.controlId}`}>
          <span>{t('Control ID')}</span>
          <code>{data.controlId}</code>
        </div>
      )}
      {isLoudness ? (
        <LoudnessView
          id={data.id}
          integrated={telemetry?.loudnessIntegrated}
          shortTerm={telemetry?.loudnessShortTerm}
          lra={telemetry?.loudnessRange}
          truePeak={telemetry?.loudnessTruePeak}
        />
      ) : isDbMeter ? (
        <div className="db-meter-body">
          <DbMeterView db={telemetry?.db} peakDb={telemetry?.peakDb} dbLeft={telemetry?.dbLeft} dbRight={telemetry?.dbRight} peakDbLeft={telemetry?.peakDbLeft} peakDbRight={telemetry?.peakDbRight} vertical={dbVertical} />
        </div>
      ) : isModifier ? (
        <div className="parameter-list">
          {data.isNoteDetector && <NoteDetectorView telemetry={telemetry} />}
          {isSpectrum && <SpectrumView
            bands={telemetry?.spectrum}
            peakHz={telemetry?.spectrumPeakHz}
            color={data.accent}
            floor={spectrumFloor}
            ceiling={spectrumCeiling}
          />}
          {isSpectrogram && <SpectrogramView
            values={telemetry?.spectrogram}
            newColumns={telemetry?.spectrogramNewColumns}
            totalColumns={telemetry?.spectrogramTotalColumns}
            full={telemetry?.spectrogramFull}
            capacity={telemetry?.spectrogramColumns}
            windowSeconds={telemetry?.spectrogramWindowSeconds}
            peakHz={telemetry?.spectrogramPeakHz}
            floor={spectrumFloor}
            ceiling={spectrumCeiling}
            contrast={spectrogramContrast}
            palette={spectrogramPalette}
          />}
          {isWaveform && <WaveformView
            samples={telemetry?.waveform}
            peak={telemetry?.waveformPeak}
            color={data.accent}
          />}
          {data.isGraphicEq && <GraphicEqualizerView
            nodeId={data.id}
            parameters={data.parameters}
            accent={data.accent}
          />}
          {data.isVst3 && <Vst3Controls node={data} />}
          {data.isTranslator && <TranslatorControls node={data} />}
          {data.isTranslator && <TranslationView
            id={data.id}
            status={telemetry?.transcriptionStatus}
            source={telemetry?.transcript}
            sourcePartial={telemetry?.partialTranscript}
            translated={telemetry?.translatedTranscript}
            translatedPartial={telemetry?.translatedPartial}
            error={telemetry?.transcriptionError}
            level={telemetry?.transcriptionLevel}
            hasAudio={telemetry?.transcriptionHasAudio}
            elapsed={telemetry?.transcriptionElapsed}
            detected={telemetry?.detectedLanguage}
            target={data.translatorTarget}
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
            detected={telemetry?.detectedLanguage}
          />}
          {data.flavor === 'chain' && !!(data.chainElements?.length) && <div className="chain-chips">
            {data.chainElements.map((name, index) => <span className="chain-chip" key={`${name}-${index}`}>{name}</span>)}
          </div>}
          {data.isConvolutionReverb && <IrControls node={data} />}
          {data.isNoiseSuppression && <NoiseProfileControls node={data} />}
          {canParallel && (
            <div className="parallel-controls nodrag">
              <label className="parallel-toggle">
                <input
                  type="checkbox"
                  checked={data.isParallel === true}
                  onChange={event => send('parallel', { id: data.id, value: event.target.checked })}
                />
                <span>{t('Parallel send')}</span>
              </label>
              {data.isParallel && (
                <label className="parallel-send">
                  <span>{t('Send')}</span>
                  <input
                    type="range"
                    min={0}
                    max={1}
                    step={0.01}
                    value={data.send ?? 0.5}
                    onChange={event => send('send', { id: data.id, value: Number(event.target.value) })}
                  />
                  <span>{Math.round((data.send ?? 0.5) * 100)}%</span>
                </label>
              )}
            </div>
          )}
          {data.isParametricEq && <ParametricEqView node={data} />}
          {!data.isGraphicEq && !data.isParametricEq && data.parameters
            .map(parameter => (
            <ParameterControl
              key={`${parameter.processorIndex}-${parameter.parameterIndex}`}
              nodeId={data.id}
              midiEnabled={data.midiEnabled === true}
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
          {data.isNetworkInput && <NetworkInputControls node={data} />}
          {data.isTts && <TtsControls node={data} />}
          {data.isFilePlayer && <FilePlayerControls node={data} telemetry={telemetry} />}
          {data.isToneGenerator && <ToneControls node={data} />}
          {data.isSynthGenerator && <SynthControls node={data} />}
          <VolumeControl id={data.id} value={data.volume} muted={data.muted} />
          {data.isNetworkOutput && <NetworkOutputControls node={data} />}
          {data.isRecorder && <RecorderControls node={data} telemetry={telemetry} />}
        </>
      )}
      {data.isNoteDetector
        ? <Handle id="key" type="source" position={Position.Right} className={`ruse-handle key-handle ${flowing ? 'active' : ''}`} />
        : data.kind !== 'output' && <Handle id="out" type="source" position={Position.Right} className={`ruse-handle ${flowing ? 'active' : ''}`} />}
    </div>
  );
}

export default memo(RuseNode);
