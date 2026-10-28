import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  AlertTriangle, AppWindow, AudioLines, Check, ChevronLeft, Info, Mic,
  MonitorSpeaker, Radio, RefreshCw,
} from 'lucide-react';
import { send, subscribe } from '../bridge';
import { useI18n } from '../i18n';
import { useNodeTelemetry } from '../telemetry';
import type { CatalogItem, CatalogSnapshot, FirstRouteResult } from '../types';
import { Modal } from './Dialogs';

type SourceKind = 'system' | 'app' | 'input';
type Step = 1 | 2 | 3;

const SYSTEM_SOURCE: CatalogItem = {
  id: 'system',
  kind: 'system',
  name: 'System audio',
  subtitle: 'Everything playing on this PC',
  details: 'Captures the default Windows output',
};

const CATEGORIES = [
  { id: 'system' as const, label: 'System audio', Icon: AudioLines },
  { id: 'app' as const, label: 'Application', Icon: AppWindow },
  { id: 'input' as const, label: 'Microphone', Icon: Mic },
];

function SourceIcon({ item }: { item: CatalogItem }) {
  if (item.iconDataUrl) return <img className="wizard-app-icon" src={item.iconDataUrl} alt="" />;
  if (item.kind === 'app') return <AppWindow size={18} />;
  if (item.kind === 'input') return <Mic size={18} />;
  return <AudioLines size={18} />;
}

function OutputIcon({ item }: { item: CatalogItem }) {
  return item.kind === 'virtual' ? <Radio size={18} /> : <MonitorSpeaker size={18} />;
}

function Choice({ item, selected, onSelect, subtitle, icon }: {
  item: CatalogItem;
  selected: boolean;
  onSelect: () => void;
  subtitle?: string;
  icon: ReactNode;
}) {
  const { t } = useI18n();
  return (
    <button type="button" className={`wizard-choice ${selected ? 'selected' : ''}`} onClick={onSelect}>
      <span className="wizard-choice-icon">{icon}</span>
      <span className="wizard-choice-copy">
        <strong>{t(item.name)}{item.active && <span className="wizard-active-dot" title={t('Playing now')} />}</strong>
        <small>{subtitle ?? t(item.subtitle)}</small>
      </span>
      {selected && <Check size={15} className="wizard-choice-check" />}
    </button>
  );
}

export default function WelcomeWizard({ catalog, onClose, onOpenHelp }: {
  catalog: CatalogSnapshot;
  onClose: () => void;
  onOpenHelp: () => void;
}) {
  const { t } = useI18n();
  const [step, setStep] = useState<Step>(1);
  const [sourceKind, setSourceKind] = useState<SourceKind>('system');
  const [sourceItem, setSourceItem] = useState<CatalogItem | null>(SYSTEM_SOURCE);
  const [outputItem, setOutputItem] = useState<CatalogItem | null>(null);
  const [query, setQuery] = useState('');
  const [acknowledged, setAcknowledged] = useState(false);
  const [result, setResult] = useState<FirstRouteResult | null>(null);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showTroubleshooting, setShowTroubleshooting] = useState(false);

  const outputs = useMemo(() => {
    const all = [...catalog.outputs, ...catalog.virtuals];
    return all.sort((a, b) => Number(b.isDefault ?? false) - Number(a.isDefault ?? false));
  }, [catalog.outputs, catalog.virtuals]);
  const apps = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return catalog.apps
      .filter(item => !needle || item.name.toLowerCase().includes(needle) || item.subtitle.toLowerCase().includes(needle))
      .slice(0, 60);
  }, [catalog.apps, query]);

  useEffect(() => {
    if (outputItem && outputs.some(item => item.id === outputItem.id)) return;
    setOutputItem(outputs.find(item => item.isDefault) ?? outputs[0] ?? null);
  }, [outputs, outputItem]);

  useEffect(() => subscribe((type, payload) => {
    if (type === 'first-route') {
      setResult(payload as FirstRouteResult);
      setCreating(false);
      setError(null);
    } else if (type === 'alert') {
      setCreating(false);
    }
  }), []);

  useEffect(() => {
    if (!creating) return;
    const timer = window.setTimeout(() => {
      setCreating(false);
      setError(t('The route took too long to create. Check the status bar and try again.'));
    }, 12000);
    return () => window.clearTimeout(timer);
  }, [creating, t]);

  const telemetry = useNodeTelemetry(result?.sourceId ?? '');
  const level = Math.max(0, Math.min(1, telemetry?.level ?? 0));
  const hasSignal = level > 0.01;
  const meterWidth = Math.round(Math.min(1, Math.pow(level, 0.65)) * 100);

  const sourceEndpoint = sourceKind === 'system'
    ? catalog.defaultOutputId
    : sourceKind === 'app'
      ? sourceItem?.deviceId ?? ''
      : '';
  const duplicateRisk = Boolean(sourceEndpoint && outputItem &&
    sourceEndpoint.toLowerCase() === outputItem.id.toLowerCase());
  const copyRisk = Boolean(sourceEndpoint && outputItem && !duplicateRisk);
  const sourceEndpointName = sourceKind === 'system'
    ? catalog.defaultOutputName
    : sourceItem?.deviceName ?? '';
  const canCreate = Boolean(sourceItem && outputItem && (!duplicateRisk || acknowledged));

  const selectCategory = (kind: SourceKind) => {
    setSourceKind(kind);
    setSourceItem(kind === 'system' ? SYSTEM_SOURCE : null);
    setAcknowledged(false);
    setResult(null);
    setError(null);
  };

  const selectSource = (item: CatalogItem) => {
    setSourceItem(item);
    setAcknowledged(false);
    setResult(null);
    setError(null);
  };

  const selectOutput = (item: CatalogItem) => {
    setOutputItem(item);
    setAcknowledged(false);
  };

  const create = () => {
    if (!sourceItem || !outputItem || !canCreate || creating) return;
    setCreating(true);
    setError(null);
    send('first-route', {
      sourceKind,
      sourceId: sourceItem.id,
      processId: sourceItem.processId ?? 0,
      outputKind: outputItem.kind === 'virtual' ? 'virtual' : 'output',
      outputId: outputItem.id,
    });
  };

  const renderSourceStep = () => (
    <>
      <p className="wizard-kicker">{t("Let's make your first route")}</p>
      <h3>{t('What do you want to route?')}</h3>
      <p className="wizard-lead">{t('Pick a source. Ruse captures it and sends it wherever you want — the original app keeps working normally.')}</p>
      <div className="wizard-categories" role="tablist" aria-label={t('Source')}>
        {CATEGORIES.map(({ id, label, Icon }) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={sourceKind === id}
            className={`wizard-category ${sourceKind === id ? 'active' : ''}`}
            onClick={() => selectCategory(id)}
          >
            <Icon size={14} /> {t(label)}
          </button>
        ))}
      </div>

      {sourceKind === 'system' && (
        <div className="wizard-list">
          <Choice item={SYSTEM_SOURCE} selected={sourceItem?.id === SYSTEM_SOURCE.id} onSelect={() => selectSource(SYSTEM_SOURCE)} icon={<AudioLines size={18} />} />
        </div>
      )}

      {sourceKind === 'app' && (
        <>
          <div className="wizard-search">
            <input value={query} onChange={event => setQuery(event.target.value)} placeholder={t('Search applications')} aria-label={t('Search applications')} />
            <button className="icon-button compact" title={t('Refresh applications')} aria-label={t('Refresh applications')} onClick={() => send('refresh')}><RefreshCw size={13} /></button>
          </div>
          <div className="wizard-list">
            {apps.map(item => (
              <Choice key={item.id} item={item} selected={sourceItem?.id === item.id} onSelect={() => selectSource(item)} icon={<SourceIcon item={item} />} subtitle={item.active ? t('Playing now') : t(item.subtitle)} />
            ))}
            {apps.length === 0 && <p className="wizard-empty">{t('No applications found. Start playback, then refresh.')}</p>}
          </div>
        </>
      )}

      {sourceKind === 'input' && (
        <>
          <div className="wizard-search">
            <span className="wizard-search-note">{t('Microphones and ASIO inputs')}</span>
            <button className="icon-button compact" title={t('Refresh inputs')} aria-label={t('Refresh inputs')} onClick={() => send('refresh')}><RefreshCw size={13} /></button>
          </div>
          <div className="wizard-list">
            {catalog.inputs.map(item => (
              <Choice key={item.id} item={item} selected={sourceItem?.id === item.id} onSelect={() => selectSource(item)} icon={<SourceIcon item={item} />} />
            ))}
            {catalog.inputs.length === 0 && <p className="wizard-empty">{t('No capture devices found.')}</p>}
          </div>
        </>
      )}

      <div className="wizard-flow" aria-hidden="true">
        <span className="wizard-flow-node">{t('Source')}</span>
        <span className="wizard-flow-arrow">{'\u2192'}</span>
        <span className="wizard-flow-node muted">{t('Effects')} <small>{t('Optional')}</small></span>
        <span className="wizard-flow-arrow">{'\u2192'}</span>
        <span className="wizard-flow-node">{t('Output')}</span>
      </div>
      <p className="wizard-muted">{t('The guide creates the source and output. Open the library later to add effects, recorders, or a transcriber.')}</p>
    </>
  );

  const renderOutputStep = () => (
    <>
      <h3>{t('Where should it go?')}</h3>
      <p className="wizard-lead">{t('Choose the device you want to hear the routed audio on.')}</p>
      <div className="wizard-list">
        {outputs.map(item => (
          <Choice
            key={item.id}
            item={item}
            selected={outputItem?.id === item.id}
            onSelect={() => selectOutput(item)}
            icon={<OutputIcon item={item} />}
            subtitle={item.id === catalog.defaultOutputId ? `${t(item.subtitle)} / ${t('Windows default')}` : t(item.subtitle)}
          />
        ))}
        {outputs.length === 0 && <p className="wizard-empty">{t('No output devices found. Connect one, then refresh.')}</p>}
      </div>
      <div className="wizard-actions-inline">
        <button className="tool-button" onClick={() => send('refresh')}><RefreshCw size={14} /> {t('Refresh devices')}</button>
      </div>
    </>
  );

  const routeSummary = (source: string, output: string) => (
    <div className="wizard-summary">
      <span className="wizard-summary-node"><AudioLines size={15} /> {source}</span>
      <span className="wizard-summary-arrow">{'\u2192'}</span>
      <span className="wizard-summary-node"><MonitorSpeaker size={15} /> {output}</span>
    </div>
  );

  const renderReviewStep = () => (
    <>
      <h3>{result ? t('Your first route is live') : t('Ready to connect')}</h3>
      <p className="wizard-lead">
        {result
          ? t('You can keep this guide open while you test the route.')
          : t('Ruse will create both nodes and connect them for you.')}
      </p>
      {routeSummary(t(result?.sourceTitle ?? sourceItem?.name ?? ''), result?.outputTitle ?? outputItem?.name ?? '')}

      {!result && duplicateRisk && (
        <div className="wizard-warning">
          <AlertTriangle size={16} />
          <div>
            <strong>{t('This setup will duplicate the sound')}</strong>
            <p>{t('{source} is already playing on {output}. Ruse would send a second copy to the same device.', { source: t(sourceItem?.name ?? ''), output: outputItem?.name ?? '' })}</p>
            <button className="wizard-link" onClick={onOpenHelp}>{t('How to fix')}</button>
            <label className="wizard-ack">
              <input type="checkbox" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />
              {t('I understand I may hear it twice')}
            </label>
          </div>
        </div>
      )}

      {!result && copyRisk && (
        <div className="wizard-copy-note">
          <Info size={16} />
          <div>
            <strong>{t('The original still plays on {device}', { device: sourceEndpointName || t('its current device') })}</strong>
            <p>{t('Ruse will send a copy to {output}. If both devices are audible, you will hear it twice. Use a virtual cable if Ruse should be the only copy.', { output: outputItem?.name ?? '' })}</p>
            <button className="wizard-link" onClick={onOpenHelp}>{t('How to fix')}</button>
          </div>
        </div>
      )}

      {result && (
        <>
          <div className={`wizard-signal ${hasSignal ? 'active' : ''}`}>
            <div className="wizard-signal-head">
              <span className="status-dot" />
              <strong>{hasSignal ? t('Signal detected') : t('Waiting for audio')}</strong>
              <span className="wizard-signal-value">{Math.round(level * 100)}%</span>
            </div>
            <div className="wizard-level"><span style={{ width: `${meterWidth}%` }} /></div>
            <p>{hasSignal ? t('The source is reaching Ruse. You should hear it on the output.') : t('Play something in {source}, then watch this meter.', { source: t(result.sourceTitle) })}</p>
          </div>
          <button className="tool-button" onClick={() => setShowTroubleshooting(value => !value)}>
            {showTroubleshooting ? t('Hide troubleshooting') : t("I can't hear it")}
          </button>
          {showTroubleshooting && (
            <ul className="wizard-troubleshoot">
              <li>{t('Turn the Routing switch on at the top of the window.')}</li>
              <li>{t('Check the output node volume and Windows volume.')}</li>
              <li>{t('Make sure {source} is actually playing audio.', { source: t(result.sourceTitle) })}</li>
              <li>{t('Open Windows sound settings and confirm the output device.')}</li>
            </ul>
          )}
          {result.warning && result.severity === 'info' && (
            <div className="wizard-copy-note">
              <Info size={16} />
              <div>
                <strong>{t('A copy is also playing elsewhere')}</strong>
                <p>{result.warning}</p>
                <button className="wizard-link" onClick={onOpenHelp}>{t('How to fix')}</button>
              </div>
            </div>
          )}
          {result.warning && result.severity !== 'info' && (
            <div className="wizard-warning">
              <AlertTriangle size={16} />
              <div>
                <strong>{t('Heads up: this route can double the audio')}</strong>
                <p>{result.warning}</p>
                <button className="wizard-link" onClick={onOpenHelp}>{t('How to fix')}</button>
              </div>
            </div>
          )}
          <div className="wizard-next-steps">
            <span>{t('Add an effect')}</span>
            <span>{t('Record to a file')}</span>
            <span>{t('Transcribe speech')}</span>
          </div>
          <p className="wizard-muted">{t('Open the library on the left to add any of these to your route.')}</p>
        </>
      )}

      {!result && (
        <p className="wizard-muted">{t('You can add effects, recorders and transcription later from the library.')}</p>
      )}
      {error && <p className="wizard-error" role="alert">{t(error)}</p>}
    </>
  );

  return (
    <Modal title={t('Welcome to Ruse')} onClose={onClose} className="wizard-modal">
      <div className="wizard-body">
        <div className="wizard-steps" aria-label={t('Progress')}>
          {[1, 2, 3].map(value => (
            <div key={value} className={`wizard-step ${step === value ? 'active' : ''} ${step > value ? 'done' : ''}`}>
              <span className="wizard-step-number">{step > value ? <Check size={12} /> : value}</span>
              <span>{value === 1 ? t('Source') : value === 2 ? t('Output') : t('Test')}</span>
            </div>
          ))}
        </div>

        <div className="wizard-content">
          {step === 1 && renderSourceStep()}
          {step === 2 && renderOutputStep()}
          {step === 3 && renderReviewStep()}
        </div>

        <div className="wizard-footer">
          <div>
            {!result && <button className="wizard-skip" onClick={onClose}>{t('Skip the tour')}</button>}
            {result && <span className="wizard-muted">{t('Reopen this guide any time from Settings.')}</span>}
          </div>
          <div className="wizard-footer-actions">
            {step > 1 && !result && <button className="tool-button" onClick={() => setStep((step - 1) as Step)}><ChevronLeft size={14} /> {t('Back')}</button>}
            {step === 1 && <button className="primary-button" disabled={!sourceItem} onClick={() => setStep(2)}>{t('Continue')}</button>}
            {step === 2 && <button className="primary-button" disabled={!outputItem} onClick={() => setStep(3)}>{t('Continue')}</button>}
            {step === 3 && !result && (
              <button className={duplicateRisk ? 'warning-button' : 'primary-button'} disabled={!canCreate || creating} onClick={create}>
                {creating ? t('Creating route…') : duplicateRisk ? t('Create route anyway') : t('Create route')}
              </button>
            )}
            {step === 3 && result && <button className="primary-button" onClick={onClose}>{t('Done')}</button>}
          </div>
        </div>
      </div>
    </Modal>
  );
}
