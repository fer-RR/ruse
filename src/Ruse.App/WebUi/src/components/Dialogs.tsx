import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { FileText, FolderOpen, X } from 'lucide-react';
import { send } from '../bridge';
import type { SettingsModel } from '../types';

function Modal({ title, onClose, children, className = '' }: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  className?: string;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    dialog?.showModal();
    return () => dialog?.close();
  }, []);

  return (
    <dialog ref={ref} className={`modal ${className}`} onCancel={event => { event.preventDefault(); onClose(); }} onClick={event => { if (event.target === ref.current) onClose(); }}>
      <div className="modal-frame">
        <header className="modal-header"><h2>{title}</h2><button className="icon-button" aria-label="Close dialog" onClick={onClose}><X size={16} /></button></header>
        {children}
      </div>
    </dialog>
  );
}

export function PromptDialog({ title, label, value, submitLabel = 'Apply', onSubmit, onClose }: {
  title: string;
  label: string;
  value: string;
  submitLabel?: string;
  onSubmit: (value: string) => void;
  onClose: () => void;
}) {
  const [text, setText] = useState(value);
  return (
    <Modal title={title} onClose={onClose} className="small-modal">
      <form onSubmit={event => { event.preventDefault(); if (text.trim()) onSubmit(text.trim()); }}>
        <label className="field-label">{label}<input autoFocus maxLength={80} value={text} onChange={event => setText(event.target.value)} /></label>
        <div className="modal-actions"><button type="button" className="tool-button" onClick={onClose}>Cancel</button><button type="submit" className="primary-button">{submitLabel}</button></div>
      </form>
    </Modal>
  );
}

export function ConfirmDialog({ title, body, confirmLabel, onConfirm, onClose }: {
  title: string;
  body: string;
  confirmLabel: string;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <Modal title={title} onClose={onClose} className="small-modal">
      <p className="modal-copy">{body}</p>
      <div className="modal-actions"><button className="tool-button" onClick={onClose}>Cancel</button><button className="danger-button" onClick={onConfirm}>{confirmLabel}</button></div>
    </Modal>
  );
}

function ToggleSetting({ label, description, checked, onChange }: {
  label: string;
  description: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className="setting-row">
      <span><strong>{label}</strong><small>{description}</small></span>
      <input type="checkbox" role="switch" checked={checked} onChange={event => onChange(event.target.checked)} />
    </label>
  );
}

export function SettingsDialog({ settings, onChange, onClose }: {
  settings: SettingsModel;
  onChange: (settings: SettingsModel) => void;
  onClose: () => void;
}) {
  const [downloading, setDownloading] = useState<string | null>(null);
  const installed = settings.transcriberModels.filter(model => model.installed);
  const selectedModel = installed.some(model => model.id === settings.transcriberModel)
    ? settings.transcriberModel
    : installed[0]?.id ?? '';

  useEffect(() => {
    if (downloading) setDownloading(null);
  }, [settings.transcriberModels]);

  return (
    <Modal title="Settings" onClose={onClose}>
      <div className="settings-section">
        <span className="eyebrow">Transcriber</span>
        <label className="setting-row">
          <span><strong>Default speech model</strong><small>Used by new Transcriber nodes; each node can override it.</small></span>
          {installed.length > 0
            ? <select value={selectedModel} onChange={event => onChange({ ...settings, transcriberModel: event.target.value })}>
                {installed.map(model => <option key={model.id} value={model.id}>{model.language} · {model.name}</option>)}
              </select>
            : <span className="model-missing">No models installed</span>}
        </label>
        <div className="transcriber-model-list">
          {settings.transcriberModels.map(model => (
            <div className="transcriber-model-row" key={model.id}>
              <span><strong>{model.language}</strong><small>{model.name} · {model.sizeMb} MB</small></span>
              {model.installed
                ? <span className="model-installed">Installed</span>
                : <button className="small-button" disabled={downloading === model.id}
                    onClick={() => { setDownloading(model.id); send('transcriber-download', { id: model.id }); }}>
                    {downloading === model.id ? 'Downloading…' : 'Download'}
                  </button>}
            </div>
          ))}
        </div>
        <p className="settings-note">Models download once and then work offline.</p>
      </div>
      <div className="settings-section">
        <span className="eyebrow">Behavior</span>
        <ToggleSetting label="Start routing on launch" description="Start the restored graph after the workspace finishes loading." checked={settings.startRoutingOnLaunch} onChange={value => onChange({ ...settings, startRoutingOnLaunch: value })} />
        <ToggleSetting label="Start with Windows" description="Add Ruse to your per-user Windows startup apps." checked={settings.startWithWindows} onChange={value => onChange({ ...settings, startWithWindows: value })} />
      </div>
      <div className="settings-section">
        <span className="eyebrow">Workspace</span>
        <ToggleSetting label="Signal glow" description="Glow active nodes and matching library items while sound is flowing." checked={settings.signalGlow} onChange={value => onChange({ ...settings, signalGlow: value })} />
        <ToggleSetting label="Highlight cables on hover" description="Brighten a cable when the pointer moves over it." checked={settings.highlightCableOnHover} onChange={value => onChange({ ...settings, highlightCableOnHover: value })} />
        <ToggleSetting label="Highlight muted nodes" description="Use a red outline when a node is muted." checked={settings.highlightMutedNodes} onChange={value => onChange({ ...settings, highlightMutedNodes: value })} />
        <label className="setting-row"><span><strong>Mini-map</strong><small>Choose when the graph overview is visible.</small></span><select value={settings.minimapMode} onChange={event => onChange({ ...settings, minimapMode: Number(event.target.value) })}><option value={0}>Always</option><option value={1}>Never</option><option value={2}>When zoomed</option></select></label>
      </div>
      <div className="settings-section">
        <span className="eyebrow">Diagnostics</span>
        <div className="settings-actions"><button className="tool-button" onClick={() => send('open-log')}><FileText size={14} /> Open log</button><button className="tool-button" onClick={() => send('open-log-folder')}><FolderOpen size={14} /> Open data folder</button></div>
        <p className="settings-note">The interface runs in WebView2. Audio capture, mixing, ASIO, and recording remain in the native engine.</p>
      </div>
      <div className="modal-actions"><button className="primary-button" onClick={onClose}>Done</button></div>
    </Modal>
  );
}

export function HelpDialog({ onClose }: { onClose: () => void }) {
  const shortcuts = [
    ['Ctrl Z', 'Undo'], ['Ctrl Y / Ctrl Shift Z', 'Redo'], ['Ctrl D', 'Duplicate selected node'],
    ['Ctrl F', 'Focus library search'], ['Ctrl S', 'Save preset'], ['Ctrl O', 'Load preset'],
    ['Ctrl 1…5', 'Jump to Sources, Outputs, Inputs, Virtuals or Effects'],
    ['Delete / Backspace', 'Remove selected nodes or cables'], ['M', 'Mute selected node'],
    ['F', 'Fit graph'], ['T', 'Tidy graph'], ['0', 'Reset zoom'], ['+ / −', 'Zoom in or out'],
    ['Escape', 'Cancel a connection or close a menu'],
    ['Middle drag', 'Pan the canvas'], ['F1 or ?', 'Show this help'],
  ];
  return (
    <Modal title="Workspace shortcuts" onClose={onClose} className="small-modal">
      <div className="shortcut-list">{shortcuts.map(([key, description]) => <div key={key}><kbd>{key}</kbd><span>{description}</span></div>)}</div>
      <p className="settings-note">Drag from a node’s OUT port to another node’s IN port. Drop an effect or saved chain directly on a cable to insert it inline. Double-click a node to rename it; double-click a direct cable to set its gain.</p>
      <div className="modal-actions"><button className="primary-button" onClick={onClose}>Done</button></div>
    </Modal>
  );
}
