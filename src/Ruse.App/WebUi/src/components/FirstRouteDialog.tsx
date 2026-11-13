import { useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle, AppWindow, Check, ChevronLeft, ChevronRight, Mic, MonitorSpeaker,
  RefreshCw, Route, Search, Speaker, Volume2,
} from 'lucide-react';
import { send } from '../bridge';
import { useI18n } from '../i18n';
import { useNodeTelemetry } from '../telemetry';
import type { CatalogItem, CatalogSnapshot, FirstRouteResult } from '../types';
import { Modal } from './Dialogs';

type SourceMode = 'system' | 'app' | 'input';

function FirstRouteSignal({ sourceId, sourceName }: { sourceId: string; sourceName: string }) {
  const { t } = useI18n();
  const telemetry = useNodeTelemetry(sourceId);
  const level = telemetry?.level ?? 0;
  const active = level > 0.0008;
  const width = active ? Math.min(100, Math.max(6, Math.sqrt(level) * 100)) : 0;
  return (
    <div className={`first-route-signal ${active ? 'active' : ''}`}>
      <div className="first-route-signal-head">
        <strong aria-live="polite">{active ? t('Signal detected') : t('Waiting for audio')}</strong>
        <span aria-hidden="true">{active ? `${Math.round(Math.min(100, Math.sqrt(level) * 100))}%` : '\u2014'}</span>
      </div>
      <div className="first-route-meter"><span style={{ width: `${width}%` }} /></div>
      <p>{active
        ? t('The source is reaching Ruse. You should hear it on the output.')
        : t('Play something in {source}, then watch this meter.', { source: sourceName })}</p>
    </div>
  );
}

export default function FirstRouteDialog({ catalog, result, onClose }: {
  catalog: CatalogSnapshot;
  result: FirstRouteResult | null;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [step, setStep] = useState<0 | 1 | 2 | 3>(0);
  const [sourceMode, setSourceMode] = useState<SourceMode>('system');
  const [sourceId, setSourceId] = useState('');
  const [sourceProcessId, setSourceProcessId] = useState(0);
  const [sourceName, setSourceName] = useState(t('System audio'));
  const [appQuery, setAppQuery] = useState('');
  const [outputId, setOutputId] = useState('');
  const [creating, setCreating] = useState(false);
  const [timedOut, setTimedOut] = useState(false);
  const [showTroubleshooting, setShowTroubleshooting] = useState(false);

  const allOutputs = useMemo(
    () => [...catalog.outputs, ...catalog.virtuals],
    [catalog.outputs, catalog.virtuals],
  );
  const selectedOutput = allOutputs.find(item => item.id === outputId);
  const outputKind = catalog.virtuals.some(item => item.id === outputId) ? 'virtual' : 'device';

  const filteredApps = useMemo(() => {
    const query = appQuery.trim().toLowerCase();
    const apps = query
      ? catalog.apps.filter(item => `${item.name} ${item.subtitle}`.toLowerCase().includes(query))
      : catalog.apps;
    return [...apps].sort((a, b) => Number(Boolean(b.active)) - Number(Boolean(a.active)) || a.name.localeCompare(b.name));
  }, [appQuery, catalog.apps]);

  useEffect(() => {
    if (allOutputs.length === 0) return;
    if (allOutputs.some(item => item.id === outputId)) return;
    const preferred = allOutputs.find(item => item.id === catalog.defaultOutputId) ?? allOutputs[0];
    setOutputId(preferred.id);
  }, [allOutputs, catalog.defaultOutputId, outputId]);

  useEffect(() => {
    if (!result) return;
    setCreating(false);
    setTimedOut(false);
    setStep(3);
  }, [result]);

  useEffect(() => {
    if (!creating) return;
    const timer = window.setTimeout(() => {
      setCreating(false);
      setTimedOut(true);
    }, 12000);
    return () => window.clearTimeout(timer);
  }, [creating]);

  const chooseMode = (mode: SourceMode) => {
    if (mode === sourceMode) return;
    setSourceMode(mode);
    setSourceId('');
    setSourceProcessId(0);
    if (mode === 'system') setSourceName(t('System audio'));
  };
  const chooseSystem = () => chooseMode('system');

  const chooseApp = (item: CatalogItem) => {
    setSourceMode('app');
    setSourceId(item.id);
    setSourceProcessId(item.processId ?? 0);
    setSourceName(item.name);
  };

  const chooseInput = (item: CatalogItem) => {
    setSourceMode('input');
    setSourceId(item.id);
    setSourceProcessId(0);
    setSourceName(item.name);
  };

  const sourceReady = sourceMode === 'system' || Boolean(sourceId);
  const createRoute = () => {
    if (!sourceReady || !selectedOutput) return;
    setCreating(true);
    setTimedOut(false);
    send('first-route', {
      sourceKind: sourceMode,
      sourceId,
      processId: sourceProcessId,
      outputKind,
      outputId: selectedOutput.id,
    });
  };

  const title = t("Let's make your first route");

  return (
    <Modal title={title} onClose={onClose} className="wide-modal first-route-modal">
      <div className="first-route-body">
        {step < 3 && (
          <div className="first-route-steps" aria-label={t('Progress')}>
            {[0, 1, 2].map(index => (
              <span key={index} className={`first-route-step ${step === index ? 'active' : ''} ${step > index ? 'done' : ''}`}>
                {step > index ? <Check size={11} /> : index + 1}
              </span>
            ))}
          </div>
        )}

        {step === 0 && (
          <>
            <h3>{t('What do you want to route?')}</h3>
            <p className="first-route-lead">{t('Pick a source. Ruse captures it and sends it wherever you want \u2014 the original app keeps working normally.')}</p>
            <div className="first-route-tabs" role="tablist" aria-label={t('What do you want to route?')}>
              <button type="button" role="tab" aria-selected={sourceMode === 'system'} className={sourceMode === 'system' ? 'active' : ''} onClick={chooseSystem}>
                <MonitorSpeaker size={15} /> {t('Everything playing on this PC')}
              </button>
              <button type="button" role="tab" aria-selected={sourceMode === 'app'} className={sourceMode === 'app' ? 'active' : ''} onClick={() => chooseMode('app')}>
                <AppWindow size={15} /> {t('Application')}
              </button>
              <button type="button" role="tab" aria-selected={sourceMode === 'input'} className={sourceMode === 'input' ? 'active' : ''} onClick={() => chooseMode('input')}>
                <Mic size={15} /> {t('Microphone')}
              </button>
            </div>

            {sourceMode === 'system' && (
              <button type="button" className="first-route-choice selected" aria-pressed={sourceMode === 'system'} onClick={chooseSystem}>
                <span className="first-route-choice-icon"><Speaker size={18} /></span>
                <span><strong>{t('Everything playing on this PC')}</strong><small>{t('Captures the default Windows output')}</small></span>
                <Check size={16} />
              </button>
            )}

            {sourceMode === 'app' && (
              <div className="first-route-picker">
                <div className="first-route-search">
                  <Search size={14} />
                  <input autoFocus value={appQuery} aria-label={t('Search applications')} onChange={event => setAppQuery(event.target.value)} placeholder={t('Search applications')} />
                  <button type="button" className="icon-button compact" title={t('Refresh applications')} aria-label={t('Refresh applications')} onClick={() => send('refresh')}><RefreshCw size={14} /></button>
                </div>
                {filteredApps.length === 0
                  ? <p className="first-route-empty">{t('No applications found. Start playback, then refresh.')}</p>
                  : <div className="first-route-list">
                      {filteredApps.map(item => (
                        <button type="button" key={item.id} className={`first-route-choice ${sourceMode === 'app' && sourceId === item.id ? 'selected' : ''}`} aria-pressed={sourceMode === 'app' && sourceId === item.id} onClick={() => chooseApp(item)}>
                          <span className="first-route-choice-icon"><AppWindow size={16} /></span>
                          <span><strong>{item.name}</strong><small>{item.subtitle}{item.active ? ` \u00b7 ${t('Playing now')}` : ''}</small></span>
                          {sourceMode === 'app' && sourceId === item.id && <Check size={16} />}
                        </button>
                      ))}
                    </div>}
              </div>
            )}

            {sourceMode === 'input' && (
              <div className="first-route-picker">
                <div className="first-route-picker-head">
                  <span>{t('Microphones and ASIO inputs')}</span>
                  <button type="button" className="icon-button compact" title={t('Refresh inputs')} aria-label={t('Refresh inputs')} onClick={() => send('refresh')}><RefreshCw size={14} /></button>
                </div>
                {catalog.inputs.length === 0
                  ? <p className="first-route-empty">{t('No capture devices found.')}</p>
                  : <div className="first-route-list">
                      {catalog.inputs.map(item => (
                        <button type="button" key={item.id} className={`first-route-choice ${sourceMode === 'input' && sourceId === item.id ? 'selected' : ''}`} aria-pressed={sourceMode === 'input' && sourceId === item.id} onClick={() => chooseInput(item)}>
                          <span className="first-route-choice-icon"><Mic size={16} /></span>
                          <span><strong>{item.name}</strong><small>{item.subtitle}</small></span>
                          {sourceMode === 'input' && sourceId === item.id && <Check size={16} />}
                        </button>
                      ))}
                    </div>}
              </div>
            )}

            <p className="first-route-note">{t('The guide creates the source and output. Open the library later to add effects, recorders, or a transcriber.')}</p>
          </>
        )}

        {step === 1 && (
          <>
            <h3>{t('Where should it go?')}</h3>
            <p className="first-route-lead">{t('Choose the device you want to hear the routed audio on.')}</p>
            <div className="first-route-list first-route-outputs">
              {allOutputs.map(item => {
                const virtual = catalog.virtuals.some(entry => entry.id === item.id);
                return (
                  <button type="button" key={item.id} className={`first-route-choice ${outputId === item.id ? 'selected' : ''}`} aria-pressed={outputId === item.id} onClick={() => setOutputId(item.id)}>
                    <span className="first-route-choice-icon">{virtual ? <Route size={16} /> : <Volume2 size={16} />}</span>
                    <span><strong>{item.name}</strong><small>{virtual ? t('Virtual output') : item.subtitle}{item.isDefault ? ` \u00b7 ${t('Windows default')}` : ''}</small></span>
                    {outputId === item.id && <Check size={16} />}
                  </button>
                );
              })}
            </div>
            {allOutputs.length === 0 && <p className="first-route-empty">{t('No output devices found. Connect one, then refresh.')}</p>}
            {catalog.virtuals.length === 0 && (
              <div className="first-route-hint">
                <strong>{t('Install a virtual bus')}</strong>
                <p>{t('Install a virtual audio device to use these routes. VB-CABLE is free donationware from VB-Audio; VoiceMeeter adds multiple buses. Third-party drivers are not bundled with Ruse.')}</p>
                <div className="virtual-bus-links">
                  <span><strong>VB-CABLE</strong><code>vb-audio.com/Cable</code></span>
                  <span><strong>VoiceMeeter</strong><code>vb-audio.com/Voicemeeter</code></span>
                </div>
              </div>
            )}
            <div className="settings-actions">
              <button type="button" className="tool-button" onClick={() => send('refresh')}><RefreshCw size={14} /> {t('Refresh devices')}</button>
            </div>
          </>
        )}

        {step === 2 && (
          <>
            <h3>{t('Ready to connect')}</h3>
            <p className="first-route-lead">{t('Ruse will create both nodes and connect them for you.')}</p>
            <div className="first-route-summary">
              <span><small>{t('Source')}</small><strong>{sourceName}</strong></span>
              <ChevronRight size={18} />
              <span><small>{t('Output')}</small><strong>{selectedOutput?.name ?? '\u2014'}</strong></span>
            </div>
            {selectedOutput?.micHint && (
              <p className="first-route-note">{t('In other apps, choose {name} as the microphone', { name: selectedOutput.micHint })}</p>
            )}
            {timedOut && (
              <p className="first-route-error" role="alert">{t('The route took too long to create. Check the status bar and try again.')}</p>
            )}
            <p className="first-route-note">{t('You can keep this guide open while you test the route.')}</p>
          </>
        )}

        {step === 3 && result && (
          <>
            <div className="first-route-success">
              <span className="first-route-success-icon"><Check size={22} /></span>
              <div><strong>{t('Your first route is live')}</strong><small>{result.sourceTitle} → {result.outputTitle}</small></div>
            </div>
            {result.warning && (
              <div className={`first-route-warning ${result.severity === 'info' ? 'info' : ''}`}>
                <AlertTriangle size={16} />
                <div><strong>{t('Heads up: this route can double the audio')}</strong><p>{result.warning}</p></div>
              </div>
            )}
            <FirstRouteSignal sourceId={result.sourceId} sourceName={result.sourceTitle} />
            {selectedOutput?.micHint && (
              <p className="first-route-note">{t('In other apps, choose {name} as the microphone', { name: selectedOutput.micHint })}</p>
            )}
            <div className="first-route-troubleshoot">
              <button type="button" className="tool-button" onClick={() => setShowTroubleshooting(value => !value)}>
                {showTroubleshooting ? t('Hide troubleshooting') : t("I can't hear it")}
              </button>
              {showTroubleshooting && (
                <ul>
                  <li>{t('Turn the Routing switch on at the top of the window.')}</li>
                  <li>{t('Check the output node volume and Windows volume.')}</li>
                  <li>{t('Make sure {source} is actually playing audio.', { source: result.sourceTitle })}</li>
                  <li>{t('Open Windows sound settings and confirm the output device.')}</li>
                </ul>
              )}
            </div>
            <p className="first-route-note">{t('You can add effects, recorders and transcription later from the library.')}</p>
          </>
        )}
      </div>

      <div className="modal-actions first-route-actions">
        {step === 0 && <>
          <button type="button" className="tool-button" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="primary-button" disabled={!sourceReady} onClick={() => setStep(1)}>{t('Next')} <ChevronRight size={14} /></button>
        </>}
        {step === 1 && <>
          <button type="button" className="tool-button" onClick={() => setStep(0)}><ChevronLeft size={14} /> {t('Back')}</button>
          <button type="button" className="primary-button" disabled={!selectedOutput} onClick={() => setStep(2)}>{t('Next')} <ChevronRight size={14} /></button>
        </>}
        {step === 2 && <>
          <button type="button" className="tool-button" onClick={() => setStep(1)} disabled={creating}><ChevronLeft size={14} /> {t('Back')}</button>
          <button type="button" className="tool-button" onClick={onClose} disabled={creating}>{t('Skip the tour')}</button>
          <button type="button" className="primary-button" onClick={createRoute} disabled={creating || !sourceReady || !selectedOutput}>
            {creating ? t('Creating route\u2026') : t('Create route')}
          </button>
        </>}
        {step === 3 && <>
          <button type="button" className="tool-button" onClick={() => send('open-sound-settings')}>{t('Open Windows sound settings')}</button>
          <button type="button" className="primary-button" onClick={onClose}>{t('Done')}</button>
        </>}
      </div>
    </Modal>
  );
}
