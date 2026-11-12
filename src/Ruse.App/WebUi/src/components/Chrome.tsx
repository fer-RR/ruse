import { useEffect, useState } from 'react';
import type { PointerEvent as ReactPointerEvent, ReactNode } from 'react';
import {
  Clock3, FolderOpen, LayoutGrid, MicOff, Minus, RefreshCw, SlidersHorizontal,
  Redo2, Save, Settings, Square, Trash2, Undo2, Volume2, VolumeX, X, Activity,
} from 'lucide-react';
import { send } from '../bridge';
import { formatGainDb, nextVolumeValue } from '../gain';
import { useI18n } from '../i18n';
import { withShortcut } from '../shortcuts';
import { useGlobalTelemetry, useRecordingSummary } from '../telemetry';
import type { StatusMessage } from '../types';
import RuseLogo from './RuseLogo';

function WindowButton({ action, label, children, danger = false }: { action: string; label: string; children: ReactNode; danger?: boolean }) {
  return <button className={`window-button ${danger ? 'close' : ''}`} aria-label={label} onClick={() => send('window', { action })}>{children}</button>;
}

export function TitleBar() {
  const { t } = useI18n();
  const recording = useRecordingSummary();

  const startDrag = (event: ReactPointerEvent<HTMLElement>) => {
    if (event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
    const startX = event.clientX;
    const startY = event.clientY;
    let done = false;
    const cleanup = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    const move = (moveEvent: PointerEvent) => {
      if (done || (moveEvent.buttons & 1) === 0) {
        cleanup();
        return;
      }
      if (Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < 4) return;
      done = true;
      cleanup();
      send('window', { action: 'drag' });
    };
    const up = () => {
      done = true;
      cleanup();
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  };

  return (
    <header
      className="titlebar"
      onPointerDown={startDrag}
      onDoubleClick={event => {
        if (!(event.target as HTMLElement).closest('button')) send('window', { action: 'maximize' });
      }}
    >
      <div className="brand-plate">
        <RuseLogo />
        <div><strong>RUSE</strong><span>AUDIO ROUTER · AR-02</span></div>
      </div>
      <div className="titlebar-divider" />
      <span className="titlebar-caption">STEREO ROUTING UNIT</span>
      {recording.count > 0 && <span className="global-rec"><span className="status-dot" /> REC {formatSeconds(recording.seconds)}{recording.count > 1 ? ` ×${recording.count}` : ''}</span>}
      <div className="window-controls">
        <WindowButton action="minimize" label={t('Minimize')}><Minus size={14} /></WindowButton>
        <WindowButton action="maximize" label={t('Maximize or restore')}><Square size={12} /></WindowButton>
        <WindowButton action="close" label={t('Close')} danger><X size={15} /></WindowButton>
      </div>
    </header>
  );
}

function ToolButton({ icon, children, title, onClick, danger = false, active = false, accent = false }: {
  icon: ReactNode;
  children: ReactNode;
  title: string;
  onClick: () => void;
  danger?: boolean;
  active?: boolean;
  accent?: boolean;
}) {
  return <button className={`tool-button ${danger ? 'danger' : ''} ${active ? 'active' : ''} ${accent && active ? 'accent' : ''}`} title={title} aria-pressed={active || undefined} onClick={onClick}>{icon}<span>{children}</span></button>;
}

export function Toolbar({ running, audioPlaying, micMuted, canUndo, canRedo, undoLabel, redoLabel, onTransport, onRefresh, onSave, onLoad, onClear, onUndo, onRedo, onTidy, onMicMute, onSettings, mixerOpen, onMixer, diagnosticsOpen, onDiagnostics, timelineOpen, onTimeline }: {
  running: boolean;
  audioPlaying: boolean;
  micMuted: boolean;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string;
  redoLabel: string;
  onTransport: (running: boolean) => void;
  onRefresh: () => void;
  onSave: () => void;
  onLoad: () => void;
  onClear: () => void;
  onUndo: () => void;
  onRedo: () => void;
  onTidy: () => void;
  onMicMute: () => void;
  onSettings: () => void;
  mixerOpen: boolean;
  onMixer: () => void;
  diagnosticsOpen: boolean;
  onDiagnostics: () => void;
  timelineOpen: boolean;
  onTimeline: () => void;
}) {
  const { t } = useI18n();
  return (
    <div className="toolbar">
      <button className={`routing-switch ${running ? 'on' : ''} ${!running && audioPlaying ? 'off-alert' : ''}`} role="switch" aria-checked={running} aria-label={t('Routing')} title={withShortcut(running ? t('Stop routing') : t('Start routing'), 'transport')} onClick={() => onTransport(!running)}>
        <span className="switch-track"><span className="switch-knob" /></span><span className="switch-state">{running ? t('ON') : t('OFF')}</span>
      </button>
      <span className="toolbar-separator" />
      <div className="toolbar-group" role="group" aria-label={t('History and layout')}>
        <button className="toolbar-icon" disabled={!canUndo} title={withShortcut(canUndo ? t('Undo {label}', { label: t(undoLabel) }) : t('Nothing to undo'), 'undo')} aria-label={t('Undo')} onClick={onUndo}><Undo2 size={15} /></button>
        <button className="toolbar-icon" disabled={!canRedo} title={withShortcut(canRedo ? t('Redo {label}', { label: t(redoLabel) }) : t('Nothing to redo'), 'redo')} aria-label={t('Redo')} onClick={onRedo}><Redo2 size={15} /></button>
        <button className="toolbar-icon" title={withShortcut(t('Tidy up the graph'), 'tidy')} aria-label={t('Tidy up the graph')} onClick={onTidy}><LayoutGrid size={15} /></button>
        <ToolButton icon={<SlidersHorizontal size={14} />} title={withShortcut(mixerOpen ? t('Hide mixer') : t('Show mixer'), 'mixer')} onClick={onMixer} active={mixerOpen} accent>{t('Mixer')}</ToolButton>
        <ToolButton icon={<Clock3 size={14} />} title={withShortcut(t('Timeline'), 'timeline')} onClick={onTimeline} active={timelineOpen} accent>{t('Timeline')}</ToolButton>
      </div>
      <span className="toolbar-separator" />
      <div className="toolbar-group" role="group" aria-label={t('Workspace actions')}>
        <ToolButton icon={<RefreshCw size={14} />} title={withShortcut(t('Refresh applications and devices'), 'refresh')} onClick={onRefresh}>{t('Refresh')}</ToolButton>
        <ToolButton icon={<Save size={14} />} title={t('Save routing preset (Ctrl S)')} onClick={onSave}>{t('Save')}</ToolButton>
        <ToolButton icon={<FolderOpen size={14} />} title={t('Load routing preset (Ctrl O)')} onClick={onLoad}>{t('Load')}</ToolButton>
        <ToolButton icon={<Trash2 size={14} />} title={t('Clear the graph')} onClick={onClear} danger>{t('Clear')}</ToolButton>
      </div>
      <div className="toolbar-spacer" />
      <div className="toolbar-group" role="group" aria-label={t('Application actions')}>
        <ToolButton icon={<MicOff size={14} />} title={withShortcut(micMuted ? t('Unmute every microphone input') : t('Mute every microphone input'), 'muteMics')} onClick={onMicMute} active={micMuted}>{micMuted ? t('Mics muted') : t('Mute mics')}</ToolButton>
        <ToolButton icon={<Activity size={14} />} title={withShortcut(diagnosticsOpen ? t('Hide diagnostics') : t('Show live diagnostics'), 'diagnostics')} onClick={onDiagnostics} active={diagnosticsOpen} accent>{t('Diagnostics')}</ToolButton>
        <ToolButton icon={<Settings size={14} />} title={withShortcut(t('Settings'), 'settings')} onClick={onSettings}>{t('Settings')}</ToolButton>
      </div>
    </div>
  );
}

function MasterVolume({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const { t } = useI18n();
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  const commit = (next: number) => {
    setLocal(next);
    onChange(next);
  };
  return (
    <label className="master-volume"><span>{t('Master')}</span><span className="volume-slider"><input type="range" min={0} max={1.5} step={0.01} value={local} aria-valuetext={formatGainDb(local)} onChange={event => commit(Number(event.target.value))} onDoubleClick={() => commit(1)} onKeyDown={event => { const next = nextVolumeValue(event, local); if (next !== null) { event.preventDefault(); commit(next); } }} /><small className="volume-db">{formatGainDb(local)}</small></span><strong>{Math.round(local * 100)}%</strong></label>
  );
}

export function StatusBar({ status }: {
  status: StatusMessage;
}) {
  const { t } = useI18n();
  const telemetry = useGlobalTelemetry();
  return (
    <footer className="statusbar">
      <div className="status-message" role={status.isError ? 'alert' : 'status'} aria-live={status.isError ? 'assertive' : 'polite'} aria-atomic="true">
        <span className={`run-dot ${telemetry.running ? 'running' : ''}`} aria-hidden="true" />
        <strong>{telemetry.running ? t('Routing') : t('Idle')}</strong>
        <span className={status.isError ? 'error-text' : ''}>{status.text}</span>
      </div>
      <div className="status-controls">
        {telemetry.clipping && <span className="clip-chip" role="status">CLIP</span>}
        <button className={`icon-button ${telemetry.masterMuted ? 'danger-text' : ''}`} title={telemetry.masterMuted ? t('Unmute all outputs') : t('Mute all outputs')} aria-label={telemetry.masterMuted ? t('Unmute all outputs') : t('Mute all outputs')} aria-pressed={telemetry.masterMuted} onClick={() => send('master-mute', { value: !telemetry.masterMuted })}>{telemetry.masterMuted ? <VolumeX size={15} /> : <Volume2 size={15} />}</button>
        <MasterVolume value={telemetry.masterGain} onChange={value => send('master-gain', { value })} />
      </div>
    </footer>
  );
}

function formatSeconds(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}
