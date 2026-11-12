import { useEffect, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent, ReactNode } from 'react';
import { Activity, AudioLines, Cable, ChevronRight, Copy, Download, Eye, EyeOff, FileText, FolderOpen, Gauge, Headphones, LayoutGrid, Plus, RefreshCw, SlidersHorizontal, Volume2, X } from 'lucide-react';
import { send } from '../bridge';
import { LANGUAGES, normalizeLanguage, useI18n } from '../i18n';
import { TRANSLATION_TARGETS } from '../languages';
import { SHORTCUTS } from '../shortcuts';
import type { CatalogItem, GraphWarning, SettingsModel } from '../types';
import ModelCatalog from './ModelCatalog';
import NumberField from './NumberField';
import TranslationModelCatalog from './TranslationModelCatalog';
import OpusModelCatalog from './OpusModelCatalog';
import TtsModelCatalog from './TtsModelCatalog';
import { formatBytes, useUpdates } from '../updates';
import { useAudioOptimizer } from '../audioOptimizer';

export function Modal({ title, onClose, children, className = '', movable = false }: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  movable?: boolean;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLDialogElement>(null);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  useEffect(() => {
    const dialog = ref.current;
    dialog?.showModal();
    return () => dialog?.close();
  }, []);

  const startMove = (event: ReactPointerEvent<HTMLElement>) => {
    if (!movable || event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
    const dialog = ref.current;
    if (!dialog) return;
    event.preventDefault();
    const rect = dialog.getBoundingClientRect();
    const maxX = Math.max(0, (window.innerWidth - rect.width) / 2);
    const maxY = Math.max(0, (window.innerHeight - rect.height) / 2);
    const startX = event.clientX;
    const startY = event.clientY;
    const origin = offset;
    const move = (moveEvent: PointerEvent) => {
      setOffset({
        x: Math.min(maxX, Math.max(-maxX, origin.x + moveEvent.clientX - startX)),
        y: Math.min(maxY, Math.max(-maxY, origin.y + moveEvent.clientY - startY)),
      });
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  return (
    <dialog
      ref={ref}
      className={`modal ${movable ? 'movable' : ''} ${className}`}
      style={movable ? { transform: `translate(${offset.x}px, ${offset.y}px)` } : undefined}
      onCancel={event => { event.preventDefault(); onClose(); }}
      onClick={event => { if (event.target === ref.current) onClose(); }}
    >
      <div className="modal-frame">
        <header className="modal-header" onPointerDown={movable ? startMove : undefined}><h2>{title}</h2><button className="icon-button" aria-label={t('Close dialog')} onClick={onClose}><X size={16} /></button></header>
        {children}
      </div>
    </dialog>
  );
}

export function PromptDialog({ title, label, value, submitLabel, onSubmit, onClose }: {
  title: string;
  label: string;
  value: string;
  submitLabel?: string;
  onSubmit: (value: string) => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [text, setText] = useState(value);
  return (
    <Modal title={title} onClose={onClose} className="small-modal">
      <form onSubmit={event => { event.preventDefault(); if (text.trim()) onSubmit(text.trim()); }}>
        <label className="field-label">{label}<input autoFocus maxLength={80} value={text} onChange={event => setText(event.target.value)} /></label>
        <div className="modal-actions"><button type="button" className="tool-button" onClick={onClose}>{t('Cancel')}</button><button type="submit" className="primary-button">{submitLabel ?? t('Apply')}</button></div>
      </form>
    </Modal>
  );
}

export function ConfirmDialog({ title, body, confirmLabel, onConfirm, onClose }: {
  title: string;
  body?: string;
  confirmLabel: string;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  return (
    <Modal title={title} onClose={onClose} className="small-modal">
      {body && <p className="modal-copy">{body}</p>}
      <div className="modal-actions"><button className="tool-button" onClick={onClose}>{t('Cancel')}</button><button className="danger-button" onClick={onConfirm}>{confirmLabel}</button></div>
    </Modal>
  );
}

export function TrayPromptDialog({ onKeep, onQuit }: {
  onKeep: () => void;
  onQuit: () => void;
}) {
  const { t } = useI18n();
  return (
    <Modal title={t('Close to tray')} onClose={onQuit} className="small-modal">
      <p className="modal-copy">{t('Keep Ruse running in the notification area when the window is closed? Audio keeps routing while it is hidden. You can change this later in Settings.')}</p>
      <div className="modal-actions">
        <button className="tool-button" onClick={onQuit}>{t('Quit Ruse')}</button>
        <button className="primary-button" onClick={onKeep}>{t('Keep in tray')}</button>
      </div>
    </Modal>
  );
}

export function RemoveFrameDialog({ frameName, nodeCount, onRemoveFrame, onRemoveWithNodes, onClose }: {
  frameName: string;
  nodeCount: number;
  onRemoveFrame: () => void;
  onRemoveWithNodes: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  return (
    <Modal title={t('Remove group')} onClose={onClose} className="small-modal">
      <p className="modal-copy">{t('“{name}” contains {count} nodes. Remove the group only, or delete the nodes inside too?', { name: frameName, count: nodeCount })}</p>
      <div className="modal-actions">
        <button className="tool-button" onClick={onClose}>{t('Cancel')}</button>
        <button className="tool-button" onClick={onRemoveFrame}>{t('Keep nodes')}</button>
        <button className="danger-button" onClick={onRemoveWithNodes}>{t('Delete nodes too')}</button>
      </div>
    </Modal>
  );
}

export function DuplicateAudioDialog({ warning, onClose }: {
  warning?: GraphWarning;
  onClose: () => void;
}) {
  const { t } = useI18n();
  return (
    <Modal title={t('Avoid hearing it twice')} onClose={onClose} className="audio-help-modal">
      <div className="audio-help">
        <div className="audio-help-problem">
          <strong>{warning?.severity === 'info'
            ? t('The original audio still plays elsewhere')
            : warning?.deviceName
              ? t('{device} is playing two copies', { device: warning.deviceName })
              : t('This route can play the same audio twice')}</strong>
          <p>{warning?.message ?? t('Windows is already playing this source, and Ruse is sending a second copy to the same device.')}</p>
        </div>
        <p className="modal-copy">{t('Pick the setup that matches what you are doing:')}</p>
        <div className="audio-help-options">
          <div>
            <span className="audio-help-icon"><Headphones size={16} /></span>
            <span><strong>{t('Use a different output')}</strong><small>{t("Send Windows' sounds to another device.")}</small></span>
          </div>
          <div>
            <span className="audio-help-icon"><Cable size={16} /></span>
            <span><strong>{t('Use a virtual cable')}</strong><small>{t('Set Windows or the app to a virtual output, capture that cable in Ruse, then send Ruse to your speakers. Ruse becomes the only audible copy.')}</small></span>
          </div>
          <div>
            <span className="audio-help-icon"><Volume2 size={16} /></span>
            <span><strong>{t('Monitoring only')}</strong><small>{t('If you intentionally want both paths, keep the route and ignore the warning.')}</small></span>
          </div>
        </div>
        <p className="settings-note">{t('Ruse cannot safely mute the original for you: the capture can follow the same mute. A virtual cable or a different output is the reliable fix.')}</p>
        <div className="modal-actions">
          <button className="tool-button" onClick={() => send('open-sound-settings')}>{t('Open Windows sound settings')}</button>
          <button className="primary-button" onClick={onClose}>{t('Got it')}</button>
        </div>
      </div>
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

const SHORTCUT_LIST: ReadonlyArray<readonly [string, string]> = [
  [SHORTCUTS.undo, 'Undo'],
  [`${SHORTCUTS.redo} / ${SHORTCUTS.redoAlt}`, 'Redo'],
  [SHORTCUTS.duplicate, 'Duplicate selected node'],
  [SHORTCUTS.search, 'Focus library search'],
  [SHORTCUTS.library, 'Show or hide the library'],
  [SHORTCUTS.save, 'Save preset'],
  [SHORTCUTS.load, 'Load preset'],
  [SHORTCUTS.mixer, 'Show or hide the mixer'],
  [SHORTCUTS.diagnostics, 'Show live diagnostics'],
  [SHORTCUTS.librarySections, 'Jump to Sources, Outputs, Inputs, Virtuals or Effects'],
  [`${SHORTCUTS.remove} / ${SHORTCUTS.removeAlt}`, 'Remove selected nodes or cables'],
  [SHORTCUTS.muteSelected, 'Mute selected node'],
  [SHORTCUTS.bypass, 'Bypass effect'],
  [SHORTCUTS.fit, 'Fit graph'],
  [SHORTCUTS.tidy, 'Tidy graph'],
  [SHORTCUTS.resetZoom, 'Reset zoom'],
  [`${SHORTCUTS.zoomIn} / ${SHORTCUTS.zoomOut}`, 'Zoom in or out'],
  [SHORTCUTS.transport, 'Start or stop routing'],
  [SHORTCUTS.muteMics, 'Mute every microphone input'],
  [SHORTCUTS.settings, 'Open Settings'],
  [SHORTCUTS.refresh, 'Refresh applications and devices'],
  [SHORTCUTS.escape, 'Cancel a connection or close a menu'],
  ['Ctrl drag', 'Draw a group frame'],
  ['Alt drag frame edge', 'Resize a group frame'],
  ['Middle drag', 'Pan the canvas'],
  [SHORTCUTS.help, 'Open Settings'],
];

const LICENSES: ReadonlyArray<readonly [string, string, string]> = [
  ['OpenAI Whisper', 'MIT', 'Speech recognition code and model weights'],
  ['Whisper.net / whisper.cpp', 'MIT', 'Whisper bindings and native runtime'],
  ['LLamaSharp / llama.cpp', 'MIT', 'Offline translation runtime'],
  ['Qwen2.5 Instruct', 'Apache-2.0', 'Offline translation model weights'],
  ['OwnAudioVst / VST 3 SDK', 'MIT', 'VST3 plugin hosting and native editor windows'],
  ['NAudio', 'MIT', 'Audio capture, effects, WASAPI and ASIO'],
  ['WebView2', 'Microsoft', 'Embedded browser runtime and SDK'],
  ['React / React DOM / React Flow', 'MIT', 'Web interface'],
  ['Lucide', 'ISC', 'Interface icons'],
];
const SETTINGS_TABS = [
  { id: 'behavior', label: 'Behavior', Icon: SlidersHorizontal },
  { id: 'workspace', label: 'Workspace', Icon: LayoutGrid },
  { id: 'audio', label: 'Audio', Icon: Headphones },
  { id: 'speech', label: 'Speech & translation', Icon: AudioLines },
  { id: 'diagnostics', label: 'Info', Icon: Activity },
] as const;

export type SettingsTab = typeof SETTINGS_TABS[number]['id'];

const SETTINGS_SECTIONS_KEY = 'ruse.settingsSections';
let sectionStates: Record<string, boolean> | null = null;

function getSectionStates(): Record<string, boolean> {
  if (sectionStates) return sectionStates;
  try {
    const raw = localStorage.getItem(SETTINGS_SECTIONS_KEY);
    const parsed = raw ? JSON.parse(raw) as unknown : null;
    if (parsed && typeof parsed === 'object') {
      sectionStates = parsed as Record<string, boolean>;
      return sectionStates;
    }
  } catch {
    // Ignore malformed preferences and fall back to open sections.
  }

  sectionStates = {};
  return sectionStates;
}

function rememberSection(title: string, open: boolean): void {
  try {
    const states = getSectionStates();
    states[title] = open;
    localStorage.setItem(SETTINGS_SECTIONS_KEY, JSON.stringify(states));
  } catch {
    // Preferences are optional; collapsing still works for this session.
  }
}

function SettingsSection({ title, summary, children }: {
  title: string;
  summary?: ReactNode;
  children: ReactNode;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(() => getSectionStates()[title] !== false);
  return (
    <div className="settings-section settings-collapse-section">
      <button
        type="button"
        className="settings-collapse"
        aria-expanded={open}
        onClick={() => setOpen(value => {
          rememberSection(title, !value);
          return !value;
        })}
      >
        <ChevronRight size={14} className="settings-collapse-chevron" />
        <span className="eyebrow">{t(title)}</span>
        {summary != null && <span className="settings-summary">{summary}</span>}
      </button>
      {open && <div className="settings-collapse-body">{children}</div>}
    </div>
  );
}

export function SettingsDialog({ settings, onChange, onClose, initialTab = 'behavior', outputs, virtualInputs = [], virtualOutputs = [], vstPlugins = [] }: {
  settings: SettingsModel;
  onChange: (settings: SettingsModel) => void;
  onClose: () => void;
  initialTab?: SettingsTab;
  outputs: CatalogItem[];
  virtualInputs?: CatalogItem[];
  virtualOutputs?: CatalogItem[];
  vstPlugins?: CatalogItem[];
}) {
  const { t } = useI18n();
  const [tab, setTab] = useState<SettingsTab>(initialTab);
  const [gpuAtOpen] = useState(settings.useGpu);
  const [showToken, setShowToken] = useState(false);
  const updates = useUpdates();
  const optimizer = useAudioOptimizer();
  const optimizerBusy = optimizer.state.status === 'running';
  const updateBusy = updates.state.status === 'checking' || updates.state.status === 'downloading' || updates.state.status === 'applying';
  useEffect(() => setTab(initialTab), [initialTab]);
  const installed = settings.transcriberModels.filter(model => model.installed);
  const selectedModel = installed.some(model => model.id === settings.transcriberModel)
    ? settings.transcriberModel
    : installed[0]?.id ?? '';
  const installedTts = settings.ttsModels.filter(model => model.installed);
  const selectedTts = installedTts.some(model => model.id === settings.ttsModel)
    ? settings.ttsModel
    : installedTts[0]?.id ?? '';
  const installedTranslators = settings.translatorModels.filter(model => model.installed);
  const selectedTranslator = installedTranslators.some(model => model.id === settings.translatorModel)
    ? settings.translatorModel
    : installedTranslators[0]?.id ?? '';

  return (
    <Modal title={t('Settings')} onClose={onClose} className="settings-modal" movable>
      <div className="settings-tabs" role="tablist" aria-label={t('Settings sections')}>
        {SETTINGS_TABS.map(({ id, label, Icon }) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            className={`settings-tab ${tab === id ? 'active' : ''}`}
            onClick={() => setTab(id)}
          >
            <Icon size={14} /> {t(label)}
          </button>
        ))}
      </div>
      <div className="settings-panel" role="tabpanel">
        {tab === 'speech' && (
          <>
            <SettingsSection title="Hardware" summary={settings.useGpu ? 'GPU' : 'CPU'}>
              <ToggleSetting
                label={t('GPU acceleration')}
                description={t('Run speech and translation models on the GPU. Requires a Vulkan-capable driver; falls back to CPU.')}
                checked={settings.useGpu}
                onChange={value => onChange({ ...settings, useGpu: value })}
              />
              <p className="settings-note">
                {settings.gpuAvailable
                  ? t('GPU: {device}', { device: settings.gpuDeviceName ?? 'Vulkan' })
                  : `${t('No Vulkan GPU detected.')}${settings.gpuStatusError ? ` ${settings.gpuStatusError}` : ''}`}
              </p>
              {settings.gpuFallbackReason && (
                <p className="settings-note gpu-restart-note">
                  {t('Last model load fell back to CPU: {reason}', { reason: settings.gpuFallbackReason })}
                </p>
              )}
              {settings.useGpu !== gpuAtOpen && (
                <p className="settings-note gpu-restart-note">
                  {t('You may need to restart Ruse for the GPU change to take effect.')}
                </p>
              )}
            </SettingsSection>
            <SettingsSection
              title="Transcribe speech"
              summary={`${t('{count} installed', { count: settings.installedModelsCount ?? installed.length })} / ${settings.installedModelsSize ?? '0 MB'}`}
            >
              <label className="setting-row">
                <span><strong>{t('Default speech model')}</strong><small>{t('Used by new Transcriber nodes; each node can override it.')}</small></span>
                {installed.length > 0
                  ? <select value={selectedModel} onChange={event => onChange({ ...settings, transcriberModel: event.target.value })}>
                      {installed.map(model => <option key={model.id} value={model.id} title={model.name}>{t(model.language)} / {t(model.tier)}</option>)}
                    </select>
                  : <span className="model-missing">{t('No models installed')}</span>}
              </label>
              <div className="settings-section-head">
                <span className="eyebrow">{t('Speech')}</span>
              </div>
              <ModelCatalog
                models={settings.transcriberModels}
                selectedModel={selectedModel}
                download={settings.transcriberDownload}
                onSelectDefault={id => onChange({ ...settings, transcriberModel: id })}
              />
            </SettingsSection>
            <SettingsSection
              title="Translation"
              summary={`${t('{count} installed', { count: settings.installedTranslatorModelsCount ?? installedTranslators.length })} / ${settings.installedTranslatorModelsSize ?? '0 MB'}`}
            >
              <label className="setting-row">
                <span><strong>{t('Translation engine')}</strong><small>{t('Qwen is flexible and works for any language pair. OPUS-MT is smaller and runs on ONNX Runtime.')}</small></span>
                <select value={settings.translationProvider} onChange={event => onChange({ ...settings, translationProvider: event.target.value })}>
                  <option value="qwen">{t('Qwen (recommended)')}</option>
                  <option value="opus">{t('OPUS-MT (ONNX)')}</option>
                </select>
              </label>
              <label className="setting-row">
                <span><strong>{t('Default translation language')}</strong><small>{t('Used by new Translator nodes; each node can override it.')}</small></span>
                <select value={settings.translatorTarget} onChange={event => onChange({ ...settings, translatorTarget: event.target.value })}>
                  <option value={normalizeLanguage(settings.uiLanguage)}>{t('Auto (my language)')}</option>
                  {TRANSLATION_TARGETS.map(target => <option key={target.code} value={target.code}>{target.label}</option>)}
                </select>
              </label>
              {settings.translationProvider === 'qwen' ? (
                <>
                  <label className="setting-row">
                    <span><strong>{t('Default translation model')}</strong><small>{t('Used when the target language is not English.')}</small></span>
                    {installedTranslators.length > 0
                      ? <select value={selectedTranslator} onChange={event => onChange({ ...settings, translatorModel: event.target.value })}>
                          {installedTranslators.map(model => <option key={model.id} value={model.id} title={model.name}>{model.name}</option>)}
                        </select>
                      : <span className="model-missing">{t('No models installed')}</span>}
                  </label>
                  <div className="settings-section-head">
                    <span className="eyebrow">{t('Translation models')}</span>
                  </div>
                  <TranslationModelCatalog
                    models={settings.translatorModels}
                    selectedModel={selectedTranslator}
                    download={settings.translatorDownload}
                    onSelectDefault={id => onChange({ ...settings, translatorModel: id })}
                  />
                </>
              ) : (
                <OpusModelCatalog models={settings.opusModels} download={settings.translatorDownload} />
              )}
            </SettingsSection>
            <SettingsSection
              title="Text to speech"
              summary={`${t('{count} installed', { count: settings.installedTtsModelsCount ?? installedTts.length })} / ${settings.installedTtsModelsSize ?? '0 MB'}`}
            >
              <label className="setting-row">
                <span><strong>{t('Default TTS model')}</strong><small>{t('Used by new Text to speech nodes; each node can use the default.')}</small></span>
                {installedTts.length > 0
                  ? <select value={selectedTts} onChange={event => onChange({ ...settings, ttsModel: event.target.value })}>
                      {installedTts.map(model => <option key={model.id} value={model.id} title={model.name}>{model.name}</option>)}
                    </select>
                  : <span className="model-missing">{t('No models installed')}</span>}
              </label>
              <TtsModelCatalog
                models={settings.ttsModels}
                selectedModel={selectedTts}
                download={settings.ttsDownload}
                onSelectDefault={id => onChange({ ...settings, ttsModel: id })}
              />
            </SettingsSection>
          </>
        )}

        {tab === 'behavior' && (
          <>
            <SettingsSection title="Startup">
              <ToggleSetting label={t('Restore last session on launch')} description={t('Reopen the graph that was saved when Ruse last closed.')} checked={settings.autoLoadSession} onChange={value => onChange({ ...settings, autoLoadSession: value })} />
              <ToggleSetting label={t('Start routing on launch')} description={t('Start the restored graph after the workspace finishes loading.')} checked={settings.startRoutingOnLaunch} onChange={value => onChange({ ...settings, startRoutingOnLaunch: value })} />
              <ToggleSetting label={t('Start with Windows')} description={t('Add Ruse to your per-user Windows startup apps.')} checked={settings.startWithWindows} onChange={value => onChange({ ...settings, startWithWindows: value })} />
              <ToggleSetting label={t('Show welcome screen on launch')} description={t('Open the welcome screen with a guided setup shortcut when Ruse starts.')} checked={settings.showWelcomeScreen} onChange={value => onChange({ ...settings, showWelcomeScreen: value })} />
              <ToggleSetting label={t('Close to tray')} description={t('Hide Ruse in the notification area when the window is closed; audio keeps routing.')} checked={settings.minimizeToTray} onChange={value => onChange({ ...settings, minimizeToTray: value })} />
              <ToggleSetting label={t('Reattach apps when they play')} description={t('When an app source restarts, reconnect it to its existing route automatically.')} checked={settings.followPlayingApps} onChange={value => onChange({ ...settings, followPlayingApps: value })} />
              <label className="setting-row"><span><strong>{t('Auto-add new apps to')}</strong><small>{t('Add a source and connect it to this output when an app starts playing.')}</small></span><select disabled={!settings.followPlayingApps} value={settings.followOutputId} onChange={event => onChange({ ...settings, followOutputId: event.target.value })}><option value="">{t('Do not auto-add')}</option>{outputs.map(output => <option key={output.id} value={output.id}>{output.name}</option>)}</select></label>
            </SettingsSection>

            <SettingsSection title="Control API" summary={settings.controlApiEnabled ? t('On') : t('Off')}>
              <ToggleSetting
                label={t('Enable control API')}
                description={t('Allow local apps to control Ruse over HTTP and WebSocket.')}
                checked={settings.controlApiEnabled}
                onChange={value => {
                  setShowToken(false);
                  onChange({ ...settings, controlApiEnabled: value });
                }}
              />
              <label className="setting-row settings-row-tight">
                <span><strong>{t('Port')}</strong><small>{t('0 chooses a free port.')}</small></span>
                <NumberField
                  value={settings.controlApiPort ?? 47100}
                  min={0}
                  max={65535}
                  step={1}
                  disabled={!settings.controlApiEnabled}
                  onChange={value => onChange({ ...settings, controlApiPort: value })}
                  ariaLabel={t('Port')}
                />
              </label>
              {settings.controlApiEnabled && (
                <>
                  <label className="setting-row settings-row-tight">
                    <span><strong>{t('API URL')}</strong></span>
                    <input
                      className="settings-text-field"
                      readOnly
                      value={settings.controlApiUrl ?? ''}
                      onFocus={event => event.currentTarget.select()}
                    />
                  </label>
                  <div className="setting-row settings-row-tight">
                    <span><strong>{t('API token')}</strong></span>
                    <span className="secret-field">
                      <input
                        className="settings-text-field"
                        type={showToken ? 'text' : 'password'}
                        readOnly
                        value={settings.controlApiToken ?? ''}
                        onFocus={event => event.currentTarget.select()}
                      />
                      <button
                        type="button"
                        className="icon-button compact"
                        title={showToken ? t('Hide token') : t('Show token')}
                        aria-label={showToken ? t('Hide token') : t('Show token')}
                        onClick={() => setShowToken(value => !value)}
                      >
                        {showToken ? <EyeOff size={14} /> : <Eye size={14} />}
                      </button>
                    </span>
                  </div>
                </>
              )}
              <p className="settings-note">{t('Changes restart the control server.')}</p>
            </SettingsSection>

            <SettingsSection title="Help">
              <div className="settings-actions">
                <button className="tool-button" onClick={() => send('show-splash')}>{t('Show welcome screen')}</button>
                <button className="tool-button" onClick={() => send('open-sound-settings')}>{t('Open Windows sound settings')}</button>
              </div>
              <p className="settings-note">{t('If you hear the same sound twice, the source is already playing on the output device. The warning banner explains how to avoid it.')}</p>
            </SettingsSection>
          </>
        )}

        {tab === 'audio' && (
          <>
            <SettingsSection title="Output safety" summary={settings.clipGuard !== false ? t('On') : t('Off')}>
              <ToggleSetting label={t('Clip guard')} description={t('Reduce peaks and soft-limit the mix before hard clipping.')} checked={settings.clipGuard !== false} onChange={value => onChange({ ...settings, clipGuard: value })} />
              <p className="settings-note">{t('The guard engages briefly on loud transients; a GUARD badge appears on the affected output.')}</p>
            </SettingsSection>

            <SettingsSection title="Audio engine" summary={settings.audioAutoMode !== false ? t('Auto audio settings') : t('Manual')}>

              <ToggleSetting label={t('Low-latency WASAPI')} description={t('Try Windows low-latency shared mode before falling back to normal shared mode. Takes effect the next time routing starts.')} checked={settings.lowLatencyWasapi !== false} onChange={value => onChange({ ...settings, lowLatencyWasapi: value })} />
              <ToggleSetting label={t('Auto audio settings')} description={t('Choose block size and buffer profiles automatically for the current machine.')} checked={settings.audioAutoMode !== false} onChange={value => onChange({ ...settings, audioAutoMode: value })} />
              <ToggleSetting
                label={`${t('Low-latency mode')} (${t('Experimental')})`}
                description={t('Use 2.5 ms blocks and a larger safety ring. May cause dropouts on some PCs. Turns auto audio settings off.')}
                checked={settings.lowLatencyMode === true}
                onChange={value => onChange({ ...settings, lowLatencyMode: value, audioAutoMode: value ? false : settings.audioAutoMode })}
              />
              <div className="setting-row settings-row-tight">
                <span>
                  <strong>{t('Find best audio settings')}</strong>
                  <small>{t('Tests audio settings for this PC. Takes about 20-30 seconds. Routing pauses and a quiet test tone plays.')}</small>
                </span>
                <button className="tool-button" onClick={optimizer.run} disabled={optimizerBusy}>
                  <Gauge size={14} /> {optimizerBusy ? t('Testing audio...') : t('Find best audio settings')}
                </button>
              </div>
              {optimizerBusy && (
                <>
                  <p className="settings-note" aria-live="polite">{optimizer.state.message || t('Testing audio...')}</p>
                  <div className="update-progress"><span style={{ width: `${Math.round((optimizer.state.progress ?? 0) * 100)}%` }} /></div>
                </>
              )}
              {optimizer.state.status === 'done' && optimizer.state.result && (
                <p className="settings-note settings-note-stack">
                  {optimizer.state.message} {optimizer.state.result.maxLatencyMs.toFixed(1)} ms
                  {!optimizer.state.result.clean && ` - ${optimizer.state.result.underruns} underrun(s)`}
                </p>
              )}
              {optimizer.state.status === 'error' && (
                <p className="settings-note update-error">{optimizer.state.error ?? optimizer.state.message}</p>
              )}
              <label className="setting-row">
                <span><strong>{t('Block size')}</strong><small>{t('Smaller blocks lower latency but increase CPU and underrun risk.')}</small></span>
                <select disabled={settings.audioAutoMode !== false} value={settings.audioBlockSizeMs ?? 5} onChange={event => onChange({ ...settings, audioBlockSizeMs: Number(event.target.value) })}>
                  <option value={2.5}>2.5 ms</option>
                  <option value={5}>5 ms</option>
                  <option value={7.5}>7.5 ms</option>
                  <option value={10}>10 ms</option>
                </select>
              </label>
              <label className="setting-row">
                <span><strong>{t('Device buffer')}</strong><small>{t('More buffer protects against dropouts; less buffer lowers latency.')}</small></span>
                <select disabled={settings.audioAutoMode !== false} value={settings.outputBufferMode ?? 'balanced'} onChange={event => onChange({ ...settings, outputBufferMode: event.target.value })}>
                  <option value="low">{t('Low')}</option>
                  <option value="balanced">{t('Balanced')}</option>
                  <option value="safe">{t('Safe')}</option>
                  <option value="manual">{t('Manual')}</option>
                </select>
              </label>
              {settings.audioAutoMode === false && settings.outputBufferMode === 'manual' && (
                <label className="setting-row"><span><strong>{t('Manual buffer')}</strong><small>{t('Target device buffer in milliseconds.')}</small></span><NumberField value={settings.outputBufferManualMs ?? 10} min={2} max={40} step={1} onChange={value => onChange({ ...settings, outputBufferManualMs: value })} ariaLabel={t('Manual buffer')} /></label>
              )}
              <label className="setting-row">
                <span><strong>{t('Engine cushion')}</strong><small>{t('Audio queued between the renderer and the output pump.')}</small></span>
                <select disabled={settings.audioAutoMode !== false} value={settings.engineCushionMode ?? 'auto'} onChange={event => onChange({ ...settings, engineCushionMode: event.target.value })}>
                  <option value="auto">{t('Auto (2 blocks)')}</option>
                  <option value="manual">{t('Manual')}</option>
                </select>
              </label>
              {settings.audioAutoMode === false && settings.engineCushionMode === 'manual' && (
                <label className="setting-row"><span><strong>{t('Manual cushion')}</strong><small>{t('Engine queue target in milliseconds.')}</small></span><NumberField value={settings.engineCushionManualMs ?? 10} min={2} max={50} step={1} onChange={value => onChange({ ...settings, engineCushionManualMs: value })} ariaLabel={t('Manual cushion')} /></label>
              )}
              {settings.audioAutoMode === false && (
                <ToggleSetting
                  label={t('Elastic ring buffer')}
                  description={t('Start with one block and grow the engine queue only after underruns. The cushion setting is the maximum.')}
                  checked={settings.elasticRing === true}
                  onChange={value => onChange({ ...settings, elasticRing: value })}
                />
              )}
              <p className="settings-note">{t('Audio engine changes restart routing when applied.')}</p>
            </SettingsSection>

            <SettingsSection
              title="Virtual buses"
              summary={virtualInputs.length + virtualOutputs.length > 0 ? `${virtualInputs.length} in / ${virtualOutputs.length} out` : t('Recommended')}
            >
              <p className="settings-note">{t('Virtual buses carry audio between apps without a physical cable. Send Ruse to a virtual output and capture its matching input in another app, or capture a virtual input to bring another app\'s audio into Ruse.')}</p>
              <div className="virtual-bus-grid">
                <div className="virtual-bus-column">
                  <div className="virtual-bus-heading"><Cable size={13} /> {t('Virtual inputs')}</div>
                  {virtualInputs.length > 0 ? (
                    <ul className="virtual-bus-list">
                      {virtualInputs.map(item => (
                        <li key={item.id}>
                          <strong title={item.name}>{item.name}</strong>
                          <small>{item.provider || item.subtitle}</small>
                        </li>
                      ))}
                    </ul>
                  ) : <p className="virtual-bus-empty">{t('None detected')}</p>}
                </div>
                <div className="virtual-bus-column">
                  <div className="virtual-bus-heading"><Cable size={13} /> {t('Virtual outputs')}</div>
                  {virtualOutputs.length > 0 ? (
                    <ul className="virtual-bus-list">
                      {virtualOutputs.map(item => (
                        <li key={item.id}>
                          <strong title={item.name}>{item.name}</strong>
                          <small>{item.provider || item.subtitle}</small>
                        </li>
                      ))}
                    </ul>
                  ) : <p className="virtual-bus-empty">{t('None detected')}</p>}
                </div>
              </div>
              {(virtualInputs.length === 0 || virtualOutputs.length === 0) && (
                <div className="virtual-bus-recommend">
                  <strong>{t('Install a virtual bus')}</strong>
                  <p>{t('Install a virtual audio device to use these routes. VB-CABLE is free donationware from VB-Audio; VoiceMeeter adds multiple buses. Third-party drivers are not bundled with Ruse.')}</p>
                  <div className="virtual-bus-links">
                    <span><strong>VB-CABLE</strong><code>vb-audio.com/Cable</code></span>
                    <span><strong>VoiceMeeter</strong><code>vb-audio.com/Voicemeeter</code></span>
                  </div>
                </div>
              )}
              <div className="settings-actions">
                <button className="tool-button" onClick={() => send('refresh')}><RefreshCw size={14} /> {t('Refresh devices')}</button>
              </div>
            </SettingsSection>

            <SettingsSection
              title={`${t('MIDI input')} (${t('Experimental')})`}
              summary={settings.midiEnabled ? (settings.midiOpen ? t('On') : t('Off')) : t('Off')}
            >
              <ToggleSetting label={t('Enable MIDI input')} description={t('Listen for notes and control changes from a MIDI device.')} checked={settings.midiEnabled === true} onChange={value => onChange({ ...settings, midiEnabled: value })} />
              <label className="setting-row settings-row-tight">
                <span><strong>{t('Device')}</strong><small>{t('Use CC messages or note on/off to control effect parameters.')}</small></span>
                <select disabled={!settings.midiEnabled} value={settings.midiInputDevice ?? ''} onChange={event => onChange({ ...settings, midiInputDevice: event.target.value })}>
                  <option value="">{t('No MIDI device')}</option>
                  {(settings.midiInputs ?? []).map(input => <option key={input.name} value={input.name}>{input.name}</option>)}
                </select>
              </label>
              <p className="settings-note settings-note-stack">
                {settings.midiEnabled
                  ? (settings.midiOpen ? t('MIDI input is open.') : (settings.midiError ?? t('No MIDI input is open.')))
                  : t('MIDI is disabled.')}
                <br />
                {t('Click the MIDI badge on an effect parameter, then move a control or press a note to bind it.')}
              </p>
              <div className="settings-actions"><button className="tool-button" onClick={() => send('midi-refresh')}>{t('Refresh MIDI devices')}</button></div>
            </SettingsSection>

            <SettingsSection title="VST3 plugins" summary={t('{count} installed', { count: vstPlugins.length })}>
              <ToggleSetting
                label={t('Isolate VST3 plugins')}
                description={t('Run each VST3 in a separate process so a crashing plugin cannot close Ruse. New VST3 nodes use this setting.')}
                checked={settings.isolateVst3}
                onChange={value => onChange({ ...settings, isolateVst3: value })}
              />
              <div className="settings-actions">
                <button className="tool-button" onClick={() => send('vst3-rescan')}><FolderOpen size={14} /> {t('Rescan VST3 plugins')}</button>
              </div>
              <div className="vst-plugin-list">
                {vstPlugins.length === 0
                  ? <p className="settings-note">{t('No VST3 effects found.')}</p>
                  : vstPlugins.map(plugin => (
                    <div key={plugin.id} title={plugin.details ?? plugin.name}>
                      <strong>{plugin.name}</strong>
                      <small>{plugin.subtitle}</small>
                    </div>
                  ))}
              </div>
            </SettingsSection>
          </>
        )}

        {tab === 'workspace' && (
          <>
            <SettingsSection title="Interface" summary={settings.theme === 'light' ? t('Light') : t('Dark')}>
              <label className="setting-row">
                <span><strong>{t('Theme')}</strong><small>{t('Choose the workspace colour scheme.')}</small></span>
                <select value={settings.theme === 'light' ? 'light' : 'dark'} onChange={event => onChange({ ...settings, theme: event.target.value === 'light' ? 'light' : 'dark' })}>
                  <option value="dark">{t('Dark')}</option>
                  <option value="light">{t('Light')}</option>
                </select>
              </label>
              <label className="setting-row">
                <span><strong>{t('Language')}</strong><small>{t('Interface language. Audio status and device names stay in English.')}</small></span>
                <select value={normalizeLanguage(settings.uiLanguage)} onChange={event => onChange({ ...settings, uiLanguage: event.target.value })}>
                  {LANGUAGES.map(language => <option key={language.id} value={language.id}>{language.label}</option>)}
                </select>
              </label>
            </SettingsSection>
            <SettingsSection title="Visual feedback">
              <ToggleSetting label={t('Signal glow')} description={t('Glow active nodes and matching library items while sound is flowing.')} checked={settings.signalGlow} onChange={value => onChange({ ...settings, signalGlow: value })} />
              <ToggleSetting label={t('Cable animation')} description={t('Show small packets moving along cables that carry signal.')} checked={settings.cableAnimation} onChange={value => onChange({ ...settings, cableAnimation: value })} />
              <ToggleSetting label={t('Animate active connectors')} description={t('Pulse the input and output dots on nodes while signal is flowing.')} checked={settings.connectorAnimation} onChange={value => onChange({ ...settings, connectorAnimation: value })} />
              <ToggleSetting label={t('Highlight cables on hover')} description={t('Brighten a cable when the pointer moves over it.')} checked={settings.highlightCableOnHover} onChange={value => onChange({ ...settings, highlightCableOnHover: value })} />
              <ToggleSetting label={t('Highlight muted nodes')} description={t('Use a red outline when a node is muted.')} checked={settings.highlightMutedNodes} onChange={value => onChange({ ...settings, highlightMutedNodes: value })} />
            </SettingsSection>
            <SettingsSection title="Navigation">
              <ToggleSetting label={t('Snap to grid')} description={t('Align dragged nodes to the 20 px canvas grid.')} checked={settings.snapToGrid} onChange={value => onChange({ ...settings, snapToGrid: value })} />
              <label className="setting-row"><span><strong>{t('Mini-map')}</strong><small>{t('Show the overview only when part of the graph is off-screen.')}</small></span><select value={settings.minimapMode === 0 ? 2 : settings.minimapMode} onChange={event => onChange({ ...settings, minimapMode: Number(event.target.value) })}><option value={2}>{t('When needed')}</option><option value={1}>{t('Never')}</option></select></label>
            </SettingsSection>
          </>
        )}

        {tab === 'diagnostics' && (
          <>
            <SettingsSection title="Diagnostics" summary={settings.showDiagnostics ? t('On') : t('Off')}>
              <ToggleSetting
                label={t('Show live diagnostics')}
                description={t('Overlay audio buffers, delays, CPU/GPU use, frame timing and engine cost while Ruse runs.')}
                checked={settings.showDiagnostics}
                onChange={value => onChange({ ...settings, showDiagnostics: value })}
              />
            </SettingsSection>
            <SettingsSection title="System" summary={settings.appVersion}>
              <dl className="diagnostics-grid">
                <div><dt>Ruse</dt><dd>{settings.appVersion ?? t('unknown')}</dd></div>
                <div><dt>.NET</dt><dd>{settings.dotNetVersion ?? t('unknown')}</dd></div>
                <div><dt>WebView2</dt><dd>{settings.webView2Version ?? t('unknown')}</dd></div>
                <div><dt>{t('WPF render tier')}</dt><dd>{settings.renderTier ?? 0}</dd></div>
                <div><dt>{t('Models')}</dt><dd>{settings.installedModelsCount ?? 0} / {settings.installedModelsSize ?? '0 MB'}</dd></div>
                <div className="wide"><dt>{t('Data folder')}</dt><dd title={settings.dataFolder}>{settings.dataFolder || '-'}</dd></div>
                <div className="wide"><dt>{t('Models folder')}</dt><dd title={settings.modelsFolder}>{settings.modelsFolder || '-'}</dd></div>
              </dl>
            </SettingsSection>
            <SettingsSection title="Updates" summary={updates.state.latestVersion ? `v${updates.state.latestVersion}` : undefined}>
              <div className="update-summary">
                <span><strong>{t('Current version')}</strong><small>{settings.appVersion || updates.state.currentVersion || t('unknown')}</small></span>
                <button className="tool-button" onClick={updates.check} disabled={updateBusy}>
                  <RefreshCw size={14} /> {t('Check for updates')}
                </button>
              </div>
              <ToggleSetting
                label={t('Check for updates automatically')}
                description={t('Check GitHub for a new release on startup and periodically while Ruse is running.')}
                checked={settings.updateAutoCheck}
                onChange={value => onChange({ ...settings, updateAutoCheck: value })}
              />
              <label className="setting-row">
                <span><strong>{t('Check frequency')}</strong><small>{t('How often to check when automatic updates are enabled.')}</small></span>
                <select
                  disabled={!settings.updateAutoCheck}
                  value={settings.updateCheckIntervalDays ?? 1}
                  onChange={event => onChange({ ...settings, updateCheckIntervalDays: Number(event.target.value) })}
                >
                  <option value={1}>{t('Every day')}</option>
                  <option value={7}>{t('Every 7 days')}</option>
                  <option value={30}>{t('Every 30 days')}</option>
                </select>
              </label>
              {updates.state.status === 'checking' && (
                <>
                  <p className="settings-note">{t('Checking for updates…')}</p>
                  <div className="update-progress indeterminate"><span /></div>
                </>
              )}
              {updates.state.status === 'current' && (
                <p className="settings-note">{t('Ruse is up to date.')}</p>
              )}
              {updates.state.status === 'available' && (
                <>
                  <p className="settings-note">{t('Version {version} is available.', { version: updates.state.latestVersion ?? '' })}</p>
                  {updates.state.notes && (
                    <>
                      <p className="settings-note settings-note-stack">{t('Release notes')}</p>
                      <div className="update-notes">{updates.state.notes}</div>
                    </>
                  )}
                  <div className="settings-actions">
                    <button className="primary-button" onClick={updates.download}><Download size={14} /> {t('Download update')}</button>
                  </div>
                </>
              )}
              {updates.state.status === 'downloading' && (
                <>
                  <p className="settings-note">{t('Downloading update… {percent}%', { percent: Math.round((updates.state.progress ?? 0) * 100) })}</p>
                  <div className="update-progress"><span style={{ width: `${Math.round((updates.state.progress ?? 0) * 100)}%` }} /></div>
                  <p className="settings-note settings-note-stack">{formatBytes(updates.state.downloadedBytes)} / {formatBytes(updates.state.totalBytes)}</p>
                </>
              )}
              {(updates.state.status === 'ready' || updates.state.status === 'applying') && (
                <>
                  <p className="settings-note">{t('The update is verified and ready. Ruse will restart to finish.')}</p>
                  {updates.state.status === 'applying' && <div className="update-progress indeterminate"><span /></div>}
                  {updates.state.status === 'ready' && (
                    <div className="settings-actions">
                      <button className="primary-button" onClick={updates.apply}><RefreshCw size={14} /> {t('Restart and update')}</button>
                    </div>
                  )}
                </>
              )}
              {updates.state.error && <p className="settings-note update-error">{t('Update failed: {message}', { message: updates.state.error })}</p>}
              <p className="settings-note">{t('Updates are downloaded from the official GitHub release page and verified with SHA-256 before installing.')}</p>
            </SettingsSection>
            <SettingsSection title="Actions">
              <div className="settings-actions">
                <button className="tool-button" onClick={() => send('open-log')}><FileText size={14} /> {t('Open log')}</button>
                <button className="tool-button" onClick={() => send('open-log-folder')}><FolderOpen size={14} /> {t('Open data folder')}</button>
                <button className="tool-button" onClick={() => send('create-desktop-shortcut')}><Plus size={14} /> {t('Add desktop shortcut')}</button>
                <button className="tool-button" onClick={() => send('copy-diagnostics')}><Copy size={14} /> {t('Copy diagnostics')}</button>
              </div>
            </SettingsSection>
            <SettingsSection title="Keyboard shortcuts" summary={SHORTCUT_LIST.length}>
              <div className="shortcut-list settings-shortcut-list">
                {SHORTCUT_LIST.map(([key, description]) => <div key={key}><kbd>{key}</kbd><span>{t(description)}</span></div>)}
              </div>
              <p className="settings-note">{t("Drag from a node's OUT port to another node's IN port. Drop an effect or saved chain directly on a cable to insert it inline. Double-click a node's name to rename it.")}</p>
            </SettingsSection>
            <SettingsSection title="Licences" summary={LICENSES.length}>
              <div className="license-list">
                {LICENSES.map(([name, licence, detail]) => (
                  <div key={name}><strong>{name}</strong><span>{licence}</span><small>{t(detail)}</small></div>
                ))}
              </div>
              <p className="settings-note">{t('OpenAI Whisper code and model weights are released under the MIT licence. Third-party notices are included with the app.')}</p>
            </SettingsSection>
          </>
        )}
      </div>
      <div className="modal-actions"><button className="primary-button" onClick={onClose}>{t('Done')}</button></div>
    </Modal>
  );
}
