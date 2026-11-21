import { memo, useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Handle, Position, useUpdateNodeInternals } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import {
  AppWindow, Activity, AudioLines, Check, ChevronDown, ChevronUp, ClipboardPaste, Combine, Copy, FileAudio, FileText, FolderOpen, GripVertical, Languages, Layers3, Mic, MonitorSpeaker,
  Play, Power, Puzzle, Radio, Save, Settings, SkipForward, SlidersHorizontal, Square, Timer, Volume2, VolumeX, X,
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
import MetronomeControls from './MetronomeControls';
import SynthControls from './SynthControls';
import TranscriptionView from './TranscriptionView';
import TranslationView from './TranslationView';
import SpectrumView from './SpectrumView';
import SpectrogramView from './SpectrogramView';
import WaveformView from './WaveformView';
import GraphicEqualizerView from './GraphicEqualizerView';
import HearingProfileView from './HearingProfileView';
import MixerControls from './MixerControls';
import NetworkChat from './NetworkChat';

const RECORD_FORMATS = [
  ['Wav24', 'WAV \u00B7 24-bit'],
  ['Wav16', 'WAV \u00B7 16-bit'],
  ['Wav32Float', 'WAV \u00B7 32-bit float'],
  ['Mp3', 'MP3 \u00B7 192 kbps'],
  ['Flac', 'FLAC \u00B7 lossless'],
] as const;
const RECORD_RATES = [44100, 48000, 96000] as const;
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'] as const;
const NOTE_OPTIONS = Array.from({ length: 128 }, (_, index) => index);

function noteName(midi: number): string {
  const note = Math.max(0, Math.min(127, Math.round(midi)));
  return NOTE_NAMES[note % 12] + String(Math.floor(note / 12) - 1);
}

function FlavorIcon({ flavor }: { flavor: string }) {
  const props = { size: 14, strokeWidth: 1.8, 'aria-hidden': true } as const;
  switch (flavor) {
    case 'app': return <AppWindow {...props} />;
    case 'mic': return <Mic {...props} />;
    case 'system': return <AudioLines {...props} />;
    case 'output': return <MonitorSpeaker {...props} />;
    case 'virtual': return <Radio {...props} />;
    case 'file': return <FileAudio {...props} />;
    case 'metronome': return <Timer {...props} />;
    case 'tone': return <AudioLines {...props} />;
    case 'synth': return <SlidersHorizontal {...props} />;
    case 'tts': return <AudioLines {...props} />;
    case 'network': return <Radio {...props} />;
    case 'timeline': return <Play {...props} />;
    case 'recorder': return <FileAudio {...props} />;
    case 'translator': return <Languages {...props} />;
    case 'vst3': return <Puzzle {...props} />;
    case 'analysis': return <AudioLines {...props} />;
    case 'mixer': return <Combine {...props} />;
    case 'note': return <FileText {...props} />;
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
  const [analyzing, setAnalyzing] = useState(false);
  const [analysisDismissed, setAnalysisDismissed] = useState(false);
  const controlsRef = useRef<HTMLDivElement>(null);
  const previousFilePath = useRef(node.filePath);
  const fileName = node.filePath ? node.filePath.split(/[\\/]/).pop() : t('No file selected');
  const hasFile = Boolean(node.filePath);
  const playing = telemetry?.filePlaying === true;
  const paused = telemetry?.filePaused === true;
  const position = telemetry?.filePositionSeconds ?? 0;
  const duration = telemetry?.fileDurationSeconds ?? 0;
  const sliderValue = seekValue ?? (duration > 0 ? Math.round((position / duration) * 1000) : 0);
  const canSeek = hasFile && duration > 0 && (playing || paused);
  const analysis = node.fileAnalysis;
  const rootNote = node.fileRootNote ?? 60;
  const targetNote = node.fileTargetNote ?? 60;
  const pitchCents = Math.round(node.filePitchCents ?? 0);
  const transpose = targetNote - rootNote + pitchCents / 100;
  const commitNote = (root: number, target: number, cents: number) =>
    send('file-player-note', { id: node.id, root, target, cents });
  const showAnalysis = Boolean(analysis) && !analysisDismissed;
  const showAnalysisPanel = analyzing || showAnalysis || Boolean(node.fileAnalysisError);

  useEffect(() => {
    if (node.fileAnalysis || node.fileAnalysisError) setAnalyzing(false);
  }, [node.fileAnalysis, node.fileAnalysisError]);

  useEffect(() => {
    if (previousFilePath.current === node.filePath) return;
    previousFilePath.current = node.filePath;
    setAnalyzing(false);
    setAnalysisDismissed(true);
  }, [node.filePath]);

  useEffect(() => {
    const root = controlsRef.current?.closest('.ruse-node');
    if (!root) return;
    root.classList.toggle('file-analysis-open', showAnalysisPanel);
    return () => root.classList.remove('file-analysis-open');
  }, [showAnalysisPanel]);

  const commitSeek = () => {
    if (seekValue === null || duration <= 0) return;
    send('file-player-seek', { id: node.id, seconds: (duration * seekValue) / 1000 });
    setSeekValue(null);
  };

  return (
    <div className="file-player-controls nodrag" ref={controlsRef}>
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
        <button
          className="small-button"
          disabled={!hasFile || analyzing}
          onClick={() => {
            setAnalysisDismissed(false);
            setAnalyzing(true);
            send('file-player-analyze', { id: node.id });
          }}
        >
          <Activity size={11} /> {analyzing ? t('Analyzing...') : t('Analyze')}
        </button>
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
      <div className="file-player-note nodrag">
        <label>
          <span>{t('Root')}</span>
          <select
            value={rootNote}
            onChange={event => {
              const nextRoot = Number(event.target.value);
              commitNote(nextRoot, Math.max(nextRoot - 24, Math.min(nextRoot + 24, targetNote)), pitchCents);
            }}
            title={t('Sample root note')}
          >
            {NOTE_OPTIONS.map(note => <option key={note} value={note}>{noteName(note)}</option>)}
          </select>
        </label>
        <button
          type="button"
          className="icon-button compact"
          title={t('Detect root note')}
          aria-label={t('Detect root note')}
          disabled={!hasFile}
          onClick={() => send('file-player-detect-note', { id: node.id })}
        ><Activity size={11} /></button>
        <label>
          <span>{t('Note')}</span>
          <select
            value={targetNote}
            onChange={event => commitNote(rootNote, Number(event.target.value), pitchCents)}
            title={t('Target note')}
          >
            {NOTE_OPTIONS
              .filter(note => note >= rootNote - 24 && note <= rootNote + 24)
              .map(note => <option key={note} value={note}>{noteName(note)}</option>)}
          </select>
        </label>
        <label className="file-player-tape" title={t('Pitch and length follow each other')}>
          <input
            type="checkbox"
            checked={node.fileTapeMode === true}
            onChange={event => send('file-player-tape', { id: node.id, value: event.target.checked })}
          />
          <span>{t('Tape')}</span>
        </label>
        <label className="file-player-cents">
          <span>{t('Fine')}</span>
          <input
            type="range"
            min={-100}
            max={100}
            step={1}
            value={pitchCents}
            onChange={event => commitNote(rootNote, targetNote, Number(event.target.value))}
            aria-label={t('Fine tune')}
          />
          <span>{pitchCents > 0 ? '+' : ''}{pitchCents}¢</span>
        </label>
        <span className="file-player-transpose" title={t('Transpose')}>
          {transpose > 0.001 ? '+' : ''}{transpose.toFixed(2)} st
        </span>
      </div>
      {showAnalysisPanel && (
        <div className="file-analysis-panel" role="region" aria-label={t('File analysis')}>
          <div className="file-analysis-head">
            <strong>{t('File analysis')}</strong>
            <button
              type="button"
              className="icon-button compact"
              title={t('Close')}
              aria-label={t('Close')}
              onClick={() => {
                setAnalysisDismissed(true);
                setAnalyzing(false);
              }}
            ><X size={12} /></button>
          </div>
          {analyzing ? (
            <p className="file-analysis-empty">{t('Analyzing...')}</p>
          ) : node.fileAnalysisError ? (
            <p className="file-analysis-error">{node.fileAnalysisError}</p>
          ) : analysis ? (
            <div className="file-analysis-grid">
              <div><span>Format</span><b>{analysis.extension || '—'} · {analysis.encoding}</b></div>
              <div><span>Duration</span><b>{formatTime(analysis.durationSeconds)}</b></div>
              <div><span>Size</span><b>{formatBytes(analysis.fileSizeBytes)}</b></div>
              <div><span>Bitrate</span><b>{analysis.bitrateKbps > 0 ? `${analysis.bitrateKbps} kbps` : '—'}</b></div>
              <div><span>Sample rate</span><b>{analysis.sampleRate} Hz</b></div>
              <div><span>Channels</span><b>{analysis.channels}</b></div>
              <div><span>Bits</span><b>{analysis.bitsPerSample || '—'}</b></div>
              <div><span>Frames</span><b>{analysis.frameCount.toLocaleString()}</b></div>
              <div><span>Peak</span><b>{formatDb(analysis.peakDb)}</b></div>
              <div><span>True peak</span><b>{formatDb(analysis.truePeakDb)}</b></div>
              <div><span>RMS</span><b>{formatDb(analysis.rmsDb)}</b></div>
              <div><span>Crest</span><b>{analysis.crestFactorDb.toFixed(1)} dB</b></div>
              <div><span>Integrated</span><b>{analysis.integratedLufs.toFixed(1)} LUFS</b></div>
              <div><span>Loudness range</span><b>{analysis.loudnessRange.toFixed(1)} LU</b></div>
              <div><span>Silence</span><b>{analysis.silencePercent.toFixed(1)}%</b></div>
              <div><span>Clipped</span><b>{analysis.clippedSamples.toLocaleString()}</b></div>
              <div><span>DC L / R</span><b>{analysis.dcOffsetLeft.toFixed(4)} / {analysis.dcOffsetRight.toFixed(4)}</b></div>
              <div><span>Correlation</span><b>{analysis.stereoCorrelation.toFixed(2)}</b></div>
              <div><span>Balance</span><b>{analysis.stereoBalanceDb.toFixed(1)} dB</b></div>
              {analysis.lastModifiedUtc && <div><span>Modified</span><b>{new Date(analysis.lastModifiedUtc).toLocaleDateString()}</b></div>}
            </div>
          ) : null}
        </div>
      )}
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
        <input className="nowheel" value="0.0.0.0" readOnly />
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
        <input className="nowheel" value={host} onChange={event => setHost(event.target.value)} onBlur={commitHost} />
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
function TextNoteControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [text, setText] = useState(node.textContent ?? '');
  const [copied, setCopied] = useState(false);
  const areaRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (document.activeElement !== areaRef.current) {
      setText(node.textContent ?? '');
    }
  }, [node.textContent]);

  useEffect(() => {
    if (text === (node.textContent ?? '')) {
      return;
    }

    const timer = window.setTimeout(() => {
      send('text-note-text', { id: node.id, text });
    }, 400);
    return () => window.clearTimeout(timer);
  }, [text, node.id, node.textContent]);

  const commit = () => {
    if (text !== (node.textContent ?? '')) {
      send('text-note-text', { id: node.id, text });
    }
  };

  const copy = async () => {
    try {
      await navigator.clipboard?.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
      setCopied(false);
    }
  };

  const paste = async () => {
    try {
      const clipboard = await navigator.clipboard?.readText();
      if (!clipboard) {
        return;
      }

      const area = areaRef.current;
      const start = area?.selectionStart ?? text.length;
      const end = area?.selectionEnd ?? start;
      const next = text.slice(0, start) + clipboard + text.slice(end);
      setText(next);
      requestAnimationFrame(() => {
        area?.focus();
        const position = start + clipboard.length;
        area?.setSelectionRange(position, position);
      });
    } catch {
      // Clipboard read can be denied; Ctrl+V still works.
    }
  };

  const lines = text.length === 0 ? 1 : text.split('\n').length;
  const words = text.trim().length === 0 ? 0 : text.trim().split(/\s+/).length;

  return (
    <>
      <div className="text-note-body nodrag">
        <div className="text-note-toolbar">
          <span className="text-note-file" title={node.textFileName ?? ''}>
            {node.textFileName || t('Unsaved note')}
          </span>
          <button type="button" className="icon-button compact" title={copied ? t('Copied') : t('Copy text')} aria-label={copied ? t('Copied') : t('Copy text')} onClick={copy}>
            {copied ? <Check size={12} /> : <Copy size={12} />}
          </button>
          <button type="button" className="icon-button compact" title={t('Paste text')} aria-label={t('Paste text')} onClick={paste}>
            <ClipboardPaste size={12} />
          </button>
          <button type="button" className="icon-button compact" title={t('Load text')} aria-label={t('Load text')} onClick={() => send('text-note-load', { id: node.id })}>
            <FolderOpen size={12} />
          </button>
          <button type="button" className="icon-button compact" title={t('Save text')} aria-label={t('Save text')} onClick={() => { commit(); send('text-note-commit', { id: node.id }); send('text-note-save', { id: node.id }); }}>
            <Save size={12} />
          </button>
        </div>
        <textarea
          ref={areaRef}
          className="text-note-input nowheel"
          value={text}
          maxLength={200000}
          spellCheck
          placeholder={t('Write notes, lyrics, or a script...')}
          aria-label={t('Text note')}
          onChange={event => setText(event.target.value)}
          onBlur={() => { commit(); send('text-note-commit', { id: node.id }); }}
        />
        <div className="text-note-footer">
          <span>{words} {t('words')}</span>
          <span>{text.length} {t('characters')}</span>
          <span>{lines} {t('lines')}</span>
        </div>
      </div>
    </>
  );
}

function NetworkLinkControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const telemetry = useNodeTelemetry(node.id);
  const [host, setHost] = useState(node.networkHost ?? '127.0.0.1');
  useEffect(() => setHost(node.networkHost ?? '127.0.0.1'), [node.networkHost]);
  const commitHost = () => {
    if (host.trim()) send('network-output-host', { id: node.id, value: host.trim() });
  };
  const voicePort = node.networkPort ?? 7890;
  const textPort = node.chatPort ?? 7895;

  return (
    <div className="network-link nodrag">
      <section className="network-card network-voice">
        <header className="network-card-head">
          <span className="network-card-icon"><Radio size={13} /></span>
          <strong>{t('Voice')}</strong>
          <span className={`network-pill ${node.muted ? '' : 'online'}`}>
            <i />
            {node.muted ? t('Disconnected') : t('Connected')}
          </span>
        </header>

        <div className="network-field-grid">
          <label className="network-field network-field-destination">
            <span>{t('Destination IP')}</span>
            <input className="nowheel" value={host} onChange={event => setHost(event.target.value)} onBlur={commitHost} />
          </label>
          <label className="network-field network-field-port">
            <span>{t('Voice port')}</span>
            <NumberField
              value={voicePort}
              min={1}
              max={65535}
              step={1}
              onChange={value => send('network-voice-port', { id: node.id, value })}
              ariaLabel={t('Voice port')}
            />
          </label>
        </div>

        {node.muted
          ? <div className="network-note">{t('Disconnected')}</div>
          : node.networkError
            ? <div className="network-note error">{node.networkError}</div>
            : <div className="network-note"><span className="network-note-dot" />{t('Voice in/out on UDP')} {voicePort} · {Math.round(node.networkBufferedMs ?? 0)} ms</div>}

        {!node.muted && telemetry && ((telemetry.networkLostPackets ?? 0) > 0 ||
          (telemetry.networkStreamResets ?? 0) > 0) && (
          <div className="network-note error">
            {t('Packets lost')} {telemetry.networkLostPackets ?? 0} · {t('Stream resets')} {telemetry.networkStreamResets ?? 0}
          </div>
        )}

        <NetworkAddress node={node} />
        <VolumeControl id={node.id} value={node.volume} muted={node.muted} />
        <button
          className={`network-connect ${node.muted ? 'off' : 'on'}`}
          onClick={() => send('mute', { id: node.id, value: !node.muted })}
        >
          {node.muted ? <Volume2 size={13} /> : <VolumeX size={13} />}
          {node.muted ? t('Connect voice') : t('Disconnect voice')}
        </button>
      </section>

      <NetworkChat
        nodeId={node.id}
        port={textPort}
        peerHost={host}
        onPortChange={value => send('network-chat-port', { id: node.id, value })}
      />
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
  const textRef = useRef<HTMLTextAreaElement>(null);
  useEffect(() => setText(node.ttsText ?? ''), [node.ttsText]);
  const installed = node.ttsModelInstalled === true;
  const sources = node.ttsSources ?? [];
  const voices = node.ttsVoices ?? [];
  const playing = node.ttsStatus === 'Generating' || node.ttsStatus === 'Speaking';
  const commitText = () => {
    if (text !== (node.ttsText ?? '')) {
      send('tts-text', { id: node.id, text });
    }
  };

  useEffect(() => {
    const area = textRef.current;
    if (!area || !playing || document.activeElement === area) return;
    const progress = Math.max(0, Math.min(node.ttsSpokenLength ?? 0, text.length));
    const ratio = progress > 0 && text.length > 0 ? progress / text.length : 1;
    area.scrollTop = Math.max(0, ratio * area.scrollHeight - area.clientHeight * 0.6);
  }, [playing, node.ttsSpokenLength, text.length]);

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
        ref={textRef}
        className="tts-text nowheel"
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
            send('tts-jump-latest', { id: node.id });
          }}
        >
          <SkipForward size={11} /> {t('Jump to latest')}
        </button>
        <button
          className={`small-button ${playing ? 'active' : ''}`}
          disabled={!installed || !text.trim() || playing}
          onClick={() => {
            commitText();
            send('tts-speak', { id: node.id });
          }}
        >
          <Volume2 size={11} /> {playing ? t('Playing') : t('Speak')}
        </button>
        <button
          className="small-button"
          disabled={!playing}
          onClick={() => send('tts-stop', { id: node.id })}
        >
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
        <button className="icon-button compact" title={`${t('Help')}: ${t('VST3 plugins')}`} aria-label={`${t('Help')}: ${t('VST3 plugins')}`} onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-explainer', { detail: 'vst3' }))}>?</button>
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
        <span>{t('Text source')}</span>
        <select
          value={node.translatorTextSourceId ?? ''}
          onChange={event => send('translator-text-source', { id: node.id, source: event.target.value })}
        >
          <option value="">{t('Audio input')}</option>
          {(node.translatorTextSources ?? []).map(source => (
            <option key={source.id} value={source.id}>{source.title}</option>
          ))}
        </select>
      </label>
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

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${Math.round(bytes)} B`;
}

function formatDb(value: number): string {
  return Number.isFinite(value) ? `${value.toFixed(1)} dB` : '—';
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
  const isNetworkLink = data.isNetworkInput === true && data.isNetworkOutput === true;
  const isTextNote = data.isTextNote === true;
  const isDbMeter = data.isDbMeter === true;
  const isLoudness = data.isLoudness === true;
  const isSpectrum = data.isSpectrum === true;
  const isSpectrogram = data.isSpectrogram === true;
  const isWaveform = data.isWaveform === true;
  const isParametricEq = data.isParametricEq === true;
  const isHearingProfile = data.isHearingProfile === true;
  const canParallel = isModifier && !isDbMeter && !isLoudness && !isSpectrum && !isSpectrogram && !isWaveform &&
    !data.isTranscriber && !data.isTranslator && !data.isVst3 && !data.isGraphicEq && !isHearingProfile && !data.isMixer;
  const dbVertical = isDbMeter && (telemetry?.dbMeterVertical ?? true);
  const enabled = data.enabled !== false;
  const invalid = data.invalid === true;
  const recording = telemetry?.recordingStatus === 'REC';
  const installedModels = data.transcriberModels?.filter(model => model.installed) ?? [];
  const selectedModel = installedModels.some(model => model.id === data.transcriberModel)
    ? data.transcriberModel
    : installedModels[0]?.id ?? '';
  const spectrumFloor = data.parameters.find(parameter => parameter.name === 'Floor')?.value ?? -100;
  const spectrumCeiling = data.parameters.find(parameter => parameter.name === 'Ceiling')?.value ?? -30;
  const spectrumBars = (data.parameters.find(parameter => parameter.name === 'Mode')?.value ?? 0) >= 1;
  const waveformGain = data.parameters.find(parameter => parameter.name === 'Display gain')?.value ?? 12;
  const spectrogramContrast = data.parameters.find(parameter => parameter.name === 'Contrast')?.value ?? 1;
  const spectrogramPalette = Math.round(data.parameters.find(parameter => parameter.name === 'Palette')?.value ?? 0);
  const formatRateValue = data.sampleRate && data.sampleRate > 0 ? data.sampleRate : 48000;
  const formatBits = data.bitsPerSample ?? 32;
  const latency = data.kind === 'source' ? telemetry?.inputLatencyMs : data.kind === 'output' ? telemetry?.outputLatencyMs : undefined;
  const flowing = enabled && !invalid && active;
  const hasDiagnostics = Boolean(data.showDiagnostics && (data.kind === 'source' || data.kind === 'output'));
  const hasBadges = !enabled || recording || data.duplicateRisk || telemetry?.clipping ||
    telemetry?.clipGuardActive || (data.isTts && data.ttsUsesGpu !== true) ||
    ((data.isTranscriber || data.isTranslator) && typeof telemetry?.usesGpu === 'boolean') ||
    hasDiagnostics;

  const classes = [
    'ruse-node',
    hasBadges ? 'has-badges' : '',
    hasDiagnostics ? 'has-diagnostics' : '',

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
    data.isMetronome ? 'metronome-node' : '',
    data.isToneGenerator ? 'tone-node' : '',
    data.isSynthGenerator ? 'synth-node' : '',
    data.isNoteDetector ? 'note-detector-node' : '',
    data.isTts ? 'tts-node' : '',
    isNetworkLink ? 'network-node' : '',
    isTextNote ? 'text-note-node' : '',
    data.isRecorder ? 'recorder-node' : '',
    isSpectrum ? 'spectrum-node' : '',
    isSpectrogram ? 'spectrogram-node' : '',
    isWaveform ? 'waveform-node' : '',
    data.isGraphicEq ? 'graphic-eq-node' : '',
    isParametricEq ? 'parametric-eq-node' : '',
    isHearingProfile ? 'hearing-profile-node' : '',
    data.isMixer ? 'mixer-node' : '',
    data.isConvolutionReverb ? 'convolution-node' : '',
    isDbMeter ? (dbVertical ? 'db-meter-node db-meter-vertical' : 'db-meter-node db-meter-horizontal') : '',
    isLoudness ? 'loudness-node' : '',
    invalid ? 'invalid' : enabled ? '' : 'disabled',
  ].filter(Boolean).join(' ');

  return (
    <div className={classes} style={{ '--accent': data.accent, ...(data.width ? { width: `${data.width}px`, minWidth: `${data.width}px`, maxWidth: `${data.width}px` } : {}) } as CSSProperties} role="group" aria-label={`${data.title}. ${t(data.subtitle)}${data.faulted ? `. ${t('Effect chain faulted')}` : ''}${invalid && data.invalidReason ? `. ${data.invalidReason}` : ''}`}>
      {!isTextNote && data.kind !== 'source' && <Handle id="in" type="target" position={Position.Left} className={`ruse-handle ${flowing ? 'active' : ''}`} />}
      {data.isTts && <Handle id="barge" type="target" position={Position.Left} className="ruse-handle sidechain-handle" style={{ top: '82%' }} />}
      {data.supportsSidechain && <Handle id="sidechain" type="target" position={Position.Left} className="ruse-handle sidechain-handle" style={{ top: '72%' }} />}
      <header className="node-header">
        <span className="node-icon"><FlavorIcon flavor={data.flavor} /></span>
        <strong
          title={invalid && data.invalidReason ? `${data.title} — ${data.invalidReason}` : data.title}
          onDoubleClick={event => {
            event.stopPropagation();
            window.dispatchEvent(new CustomEvent('ruse-rename-node', { detail: { id: data.id, title: data.title, chain: data.flavor === 'chain' } }));
          }}
        >{data.title}</strong>
        {!enabled && <span className="node-badge bypass-badge">BYPASS</span>}
        {data.faulted && <span className="node-badge fault-badge" title={t('Effect chain faulted')}>FAULT</span>}
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
        {data.showDiagnostics && (data.kind === 'source' || data.kind === 'output') && typeof latency === 'number' && Number.isFinite(latency) ? (
          <span className="node-badge latency-badge" title={t('Latency')}>{data.kind === 'source' ? 'IN' : 'OUT'} {latency.toFixed(1)} ms</span>
        ) : null}
        {data.showDiagnostics && data.kind === 'source' && telemetry?.inputResampling && telemetry.inputSampleRate ? (
          <span className="node-badge latency-badge" title={`SRC ${telemetry.inputResamplerLatencyMs?.toFixed(1) ?? '0.0'} ms`}>
            SRC {formatRate(telemetry.inputSampleRate)} → 48k
          </span>
        ) : null}
        {data.showDiagnostics && data.kind === 'output' && telemetry?.outputResampling && telemetry.outputSampleRate ? (
          <span className="node-badge latency-badge" title="SRC">
            SRC 48k → {formatRate(telemetry.outputSampleRate)}
          </span>
        ) : null}
        {data.isMetronome || data.isToneGenerator || data.isSynthGenerator || (data.isFilePlayer && data.filePath) ? (
          <span
            className="node-timeline-drag nodrag"
            draggable
            title={t('Drag to timeline')}
            onDragStart={event => {
              event.stopPropagation();
              if (data.isFilePlayer && data.filePath) {
                event.dataTransfer.setData('application/ruse-recording', data.filePath);
              } else {
                event.dataTransfer.setData('application/ruse-generator', JSON.stringify({ id: data.id, title: data.title }));
              }
              event.dataTransfer.effectAllowed = 'copy';
            }}
          ><GripVertical size={13} /></span>
        ) : null}
        {data.supportsSidechain && <button className="node-help nodrag" title={`${t('Help')}: ${t('Sidechain ducker')}`} aria-label={`${t('Help')}: ${t('Sidechain ducker')}`} onClick={() => window.dispatchEvent(new CustomEvent('ruse-open-explainer', { detail: 'sidechain' }))}>?</button>}
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
      {data.flavor === 'app' && data.processId != null && (
        <button className="node-process nodrag" title={t('Choose process')} onClick={() => window.dispatchEvent(new CustomEvent('ruse-pick-process', { detail: { id: data.id, title: data.title, processId: data.processId, processName: data.processName } }))}>
          PID {data.processId}
        </button>
      )}
      {!isDbMeter && !isLoudness && data.micHint && <div className="node-hint" title={data.micHint}>{t('In other apps, choose {name} as the microphone', { name: data.micHint })}</div>}
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
      ) : isTextNote ? (
        <TextNoteControls node={data} />
      ) : isModifier ? (
        <div className="parameter-list">
          {data.isNoteDetector && <NoteDetectorView telemetry={telemetry} />}
          {isSpectrum && <SpectrumView
            bands={telemetry?.spectrum}
            peakHz={telemetry?.spectrumPeakHz}
            color={data.accent}
            floor={spectrumFloor}
            ceiling={spectrumCeiling}
            bars={spectrumBars}
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
            gainDb={waveformGain}
          />}
          {data.isMixer && <MixerControls node={data} telemetry={telemetry} />}
          {data.isGraphicEq && <GraphicEqualizerView
            nodeId={data.id}
            parameters={data.parameters}
            accent={data.accent}
          />}
          {isHearingProfile && <HearingProfileView
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
          {!data.isMixer && !data.isGraphicEq && !data.isParametricEq && !isHearingProfile && data.parameters
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
          {isNetworkLink
            ? <NetworkLinkControls node={data} />
            : data.isNetworkInput && <NetworkInputControls node={data} />}
          {data.isTts && <TtsControls node={data} />}
          {data.isFilePlayer && <FilePlayerControls node={data} telemetry={telemetry} />}
          {data.isMetronome && <MetronomeControls node={data} telemetry={telemetry} />}
          {data.isToneGenerator && <ToneControls node={data} />}
          {data.isSynthGenerator && <SynthControls node={data} />}
          {!isNetworkLink && !isTextNote && <VolumeControl id={data.id} value={data.volume} muted={data.muted} />}
          {data.isNetworkOutput && !data.isNetworkInput && <NetworkOutputControls node={data} />}
          {data.isRecorder && <RecorderControls node={data} telemetry={telemetry} />}
        </>
      )}
      {!isTextNote && (data.isNoteDetector
        ? <Handle id="key" type="source" position={Position.Right} className={`ruse-handle key-handle ${flowing ? 'active' : ''}`} />
        : data.kind !== 'output' && <Handle id="out" type="source" position={Position.Right} className={`ruse-handle ${flowing ? 'active' : ''}`} />)}
    </div>
  );
}

export default memo(RuseNode);
