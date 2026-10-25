import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import {
  CircleHelp, FolderOpen, LayoutGrid, MicOff, Minus, RefreshCw,
  Redo2, Save, Settings, Square, Trash2, Undo2, Volume2, VolumeX, X,
} from 'lucide-react';
import { send } from '../bridge';
import { useGlobalTelemetry, useRecordingSummary } from '../telemetry';
import type { StatusMessage } from '../types';

function WindowButton({ action, label, children, danger = false }: { action: string; label: string; children: ReactNode; danger?: boolean }) {
  return <button className={`window-button ${danger ? 'close' : ''}`} aria-label={label} onClick={() => send('window', { action })}>{children}</button>;
}

export function TitleBar() {
  const recording = useRecordingSummary();
  return (
    <header
      className="titlebar"
      onMouseDown={event => {
        if (event.button === 0 && !(event.target as HTMLElement).closest('button')) send('window', { action: 'drag' });
      }}
      onDoubleClick={event => {
        if (!(event.target as HTMLElement).closest('button')) send('window', { action: 'maximize' });
      }}
    >
      <div className="brand-plate">
        <span className="brand-led" />
        <div><strong>RUSE</strong><span>AUDIO ROUTER · AR-02</span></div>
      </div>
      <div className="titlebar-divider" />
      <span className="titlebar-caption">STEREO ROUTING UNIT</span>
      {recording.count > 0 && <span className="global-rec"><span className="status-dot" /> REC {formatSeconds(recording.seconds)}{recording.count > 1 ? ` ×${recording.count}` : ''}</span>}
      <div className="window-controls">
        <WindowButton action="minimize" label="Minimize"><Minus size={14} /></WindowButton>
        <WindowButton action="maximize" label="Maximize or restore"><Square size={12} /></WindowButton>
        <WindowButton action="close" label="Close" danger><X size={15} /></WindowButton>
      </div>
    </header>
  );
}

function ToolButton({ icon, children, title, onClick, danger = false, active = false }: {
  icon: ReactNode;
  children: ReactNode;
  title: string;
  onClick: () => void;
  danger?: boolean;
  active?: boolean;
}) {
  return <button className={`tool-button ${danger ? 'danger' : ''} ${active ? 'active' : ''}`} title={title} onClick={onClick}>{icon}<span>{children}</span></button>;
}

export function Toolbar({ running, micMuted, canUndo, canRedo, undoLabel, redoLabel, onTransport, onRefresh, onSave, onLoad, onClear, onUndo, onRedo, onTidy, onMicMute, onSettings, onHelp }: {
  running: boolean;
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
  onHelp: () => void;
}) {
  return (
    <div className="toolbar">
      <button className={`routing-switch ${running ? 'on' : ''}`} role="switch" aria-checked={running} aria-label="Routing" title={running ? 'Stop routing' : 'Start routing'} onClick={() => onTransport(!running)}>
        <span className="switch-track"><span className="switch-knob" /></span><span>Routing</span><span className="switch-state">{running ? 'LIVE' : 'OFF'}</span>
      </button>
      <span className="toolbar-separator" />
      <div className="toolbar-group" aria-label="History and layout">
        <button className="toolbar-icon" disabled={!canUndo} title={canUndo ? `Undo ${undoLabel.toLowerCase()}` : 'Nothing to undo'} aria-label="Undo" onClick={onUndo}><Undo2 size={15} /></button>
        <button className="toolbar-icon" disabled={!canRedo} title={canRedo ? `Redo ${redoLabel.toLowerCase()}` : 'Nothing to redo'} aria-label="Redo" onClick={onRedo}><Redo2 size={15} /></button>
        <button className="toolbar-icon" title="Tidy up the graph" aria-label="Tidy up the graph" onClick={onTidy}><LayoutGrid size={15} /></button>
      </div>
      <span className="toolbar-separator" />
      <div className="toolbar-group" aria-label="Workspace actions">
        <ToolButton icon={<RefreshCw size={14} />} title="Refresh applications and devices" onClick={onRefresh}>Refresh</ToolButton>
        <ToolButton icon={<Save size={14} />} title="Save routing preset (Ctrl S)" onClick={onSave}>Save</ToolButton>
        <ToolButton icon={<FolderOpen size={14} />} title="Load routing preset (Ctrl O)" onClick={onLoad}>Load</ToolButton>
        <ToolButton icon={<Trash2 size={14} />} title="Clear the graph" onClick={onClear} danger>Clear</ToolButton>
      </div>
      <div className="toolbar-spacer" />
      <div className="toolbar-group" aria-label="Application actions">
        <ToolButton icon={<MicOff size={14} />} title={micMuted ? 'Unmute every microphone input' : 'Mute every microphone input'} onClick={onMicMute} active={micMuted}>{micMuted ? 'Mics muted' : 'Mute mics'}</ToolButton>
        <ToolButton icon={<CircleHelp size={14} />} title="Keyboard shortcuts (F1)" onClick={onHelp}>Shortcuts</ToolButton>
        <ToolButton icon={<Settings size={14} />} title="Settings" onClick={onSettings}>Settings</ToolButton>
      </div>
    </div>
  );
}

function MasterVolume({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  return (
    <label className="master-volume"><span>Master</span><input type="range" min={0} max={1.5} step={0.01} value={local} onChange={event => { const next = Number(event.target.value); setLocal(next); onChange(next); }} /><strong>{Math.round(local * 100)}%</strong></label>
  );
}

export function StatusBar({ status, zoom, onZoomReset }: {
  status: StatusMessage;
  zoom: number;
  onZoomReset: () => void;
}) {
  const telemetry = useGlobalTelemetry();
  return (
    <footer className="statusbar">
      <div className="status-message"><span className={`run-dot ${telemetry.running ? 'running' : ''}`} /><strong>{telemetry.running ? 'Routing' : 'Idle'}</strong><span className={status.isError ? 'error-text' : ''}>{status.text}</span></div>
      <div className="status-controls">
        {telemetry.clipping && <span className="clip-chip">CLIP</span>}
        <button className={`icon-button ${telemetry.masterMuted ? 'danger-text' : ''}`} title={telemetry.masterMuted ? 'Unmute all outputs' : 'Mute all outputs'} onClick={() => send('master-mute', { value: !telemetry.masterMuted })}>{telemetry.masterMuted ? <VolumeX size={15} /> : <Volume2 size={15} />}</button>
        <MasterVolume value={telemetry.masterGain} onChange={value => send('master-gain', { value })} />
        <button className="zoom-readout" title="Reset zoom to 100%" onClick={onZoomReset}>{Math.round(zoom * 100)}%</button>
      </div>
    </footer>
  );
}

function formatSeconds(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}
