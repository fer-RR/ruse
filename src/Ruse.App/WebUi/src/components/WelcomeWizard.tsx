import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  AlertTriangle, AppWindow, AudioLines, Cable, Check, ChevronLeft, ChevronRight,
  CircleHelp, Mic, MonitorSpeaker, Power, Radio, RefreshCw, SlidersHorizontal, Volume2,
} from 'lucide-react';
import { send, subscribe } from '../bridge';
import { LANGUAGES, normalizeLanguage, useI18n } from '../i18n';
import { useNodeTelemetry } from '../telemetry';
import type { CatalogItem, CatalogSnapshot, FirstRouteResult, SettingsModel } from '../types';
import { Modal } from './Dialogs';
import RuseLogo from './RuseLogo';

type SourceKind = 'system' | 'app' | 'input';
type Step = 0 | 1 | 2 | 3 | 4;

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

function Choice({ item, selected, onSelect, subtitle, icon, badge }: {
  item: CatalogItem;
  selected: boolean;
  onSelect: () => void;
  subtitle?: string;
  icon: ReactNode;
  badge?: string;
}) {
  const { t } = useI18n();
  return (
    <button type="button" className={`wizard-choice ${selected ? 'selected' : ''}`} onClick={onSelect}>
      <span className="wizard-choice-icon">{icon}</span>
      <span className="wizard-choice-copy">
        <strong>{t(item.name)}{badge && <span className="wizard-badge">{badge}</span>}{item.active && <span className="wizard-active-dot" title={t('Playing now')} />}</strong>
        <small>{subtitle ?? t(item.subtitle)}</small>
      </span>
      {selected && <Check size={15} className="wizard-choice-check" />}
    </button>
  );
}

export default function WelcomeWizard({ catalog, settings, onChange, onClose, onOpenHelp }: {
  catalog: CatalogSnapshot;
  settings: SettingsModel;
  onChange: (settings: SettingsModel) => void;
  onClose: () => void;
  onOpenHelp: () => void;
}) {
  const { t } = useI18n();
  const [step, setStep] = useState<Step>(0);
  const hideNextTime = !settings.showWelcomeScreen;
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
    setResult(null);
    setError(null);
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

  const renderIntroStep = () => (
    <div className="welcome-splash wizard-intro">
      <div className="welcome-splash-hero">
        <span className="welcome-splash-logo"><RuseLogo size={54} /></span>
        <div>
          <p className="wizard-kicker">{t('Sound routing, simplified')}{settings.appVersion ? ` \u00B7 ${settings.appVersion}` : ''}</p>
          <h2>{t('Send sound anywhere.')}</h2>
          <p>{t('Ruse sends the sound from one app, the whole PC, or a microphone to any speaker, headphones or virtual device. It makes a copy, so the original app keeps working normally.')}</p>
        </div>
      </div>

      <div className="welcome-splash-cards">
        <div className="welcome-splash-card">
          <span className="welcome-splash-card-icon blue"><AppWindow size={18} /></span>
          <strong>{t('1. Pick a sound')}</strong>
          <small>{t('An application, everything Windows plays, or a microphone.')}</small>
        </div>
        <div className="welcome-splash-card">
          <span className="welcome-splash-card-icon amber"><SlidersHorizontal size={18} /></span>
          <strong>{t('2. Shape it')}</strong>
          <small>{t('Adjust volume, add effects, or record and transcribe the mix.')}</small>
        </div>
        <div className="welcome-splash-card">
          <span className="welcome-splash-card-icon green"><MonitorSpeaker size={18} /></span>
          <strong>{t('3. Send it anywhere')}</strong>
          <small>{t('Speakers, headphones, a virtual cable, or another app.')}</small>
        </div>
      </div>

      <div className="welcome-splash-note">
        <AudioLines size={18} />
        <div>
          <strong>{t('Start with a guided setup')}</strong>
          <p>{t('We will pick a sound, choose where it comes out, and test the connection together. You can skip it and explore the canvas on your own.')}</p>
        </div>
      </div>
    </div>
  );

  const renderSourceStep = () => (
    <>
      <p className="wizard-kicker">{t("Let's send your first sound")}</p>
      <h3>{t('What do you want to hear?')}</h3>
      <p className="wizard-lead">{t('Ruse sends sound from one place to another. Pick an app, the whole PC, or a microphone; the original app keeps working.')}</p>
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
          <Choice item={SYSTEM_SOURCE} selected={sourceItem?.id === SYSTEM_SOURCE.id} onSelect={() => selectSource(SYSTEM_SOURCE)} icon={<AudioLines size={18} />} badge={t('Easiest')} />
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
            {apps.length === 0 && <p className="wizard-empty">{catalog.perAppCaptureAvailable ? t('No applications found. Start playback, then refresh.') : t('Per-app capture needs Windows 10 build 20348 or later. Use System audio instead.')}</p>}
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
      <h3>{t('Where do you want to hear it?')}</h3>
      <p className="wizard-lead">{t('Choose your speakers, headphones, or a virtual device. This is where the copied sound comes out.')}</p>
      <div className="wizard-list">
        {outputs.map(item => (
          <Choice
            key={item.id}
            item={item}
            selected={outputItem?.id === item.id}
            onSelect={() => selectOutput(item)}
            icon={<OutputIcon item={item} />}
            subtitle={item.id === catalog.defaultOutputId ? `${t(item.subtitle)} / ${t('Windows default')}` : t(item.subtitle)}
            badge={item.id === catalog.defaultOutputId ? t('Recommended') : undefined}
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
      <h3>{result ? t('Your first connection is live') : t('Ready to connect')}</h3>
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
          <div className="wizard-test-actions">
            <button className="tool-button" onClick={() => setShowTroubleshooting(value => !value)}>
              {showTroubleshooting ? t('Hide troubleshooting') : t("I can't hear it")}
            </button>
            <button className="tool-button" onClick={onOpenHelp}>{t('I can hear it twice')}</button>
          </div>
          {showTroubleshooting && (
            <ul className="wizard-troubleshoot">
              <li>{t('Turn the Routing switch on at the top of the window.')}</li>
              <li>{t('Check the output node volume and Windows volume.')}</li>
              <li>{t('Make sure {source} is actually playing audio.', { source: t(result.sourceTitle) })}</li>
              <li>{t('Open Windows sound settings and confirm the output device.')}</li>
            </ul>
          )}
          {result.warning && (
            <div className="wizard-warning">
              <AlertTriangle size={16} />
              <div>
                <strong>{t('Heads up: this route can double the audio')}</strong>
                <p>{result.warning}</p>
                <button className="wizard-link" onClick={onOpenHelp}>{t('How to fix')}</button>
              </div>
            </div>
          )}
        </>
      )}

      {!result && (
        <p className="wizard-muted">{t('You can add effects, recorders and transcription later from the library.')}</p>
      )}
      {error && <p className="wizard-error" role="alert">{t(error)}</p>}
    </>
  );

  const renderBasicsStep = () => (
    <>
      <p className="wizard-kicker">{t("You're ready")}</p>
      <h3>{t('Four things to remember')}</h3>
      <p className="wizard-lead">{t('Your route is already running. Play something in the source and you should hear it on the output.')}</p>
      <div className="wizard-basics">
        <div className="wizard-basic">
          <span className="wizard-basic-icon"><AppWindow size={17} /></span>
          <div>
            <strong>{t('Build with the Library')}</strong>
            <p>{t('Drag apps, devices, effects and recorders from the left side onto the canvas.')}</p>
          </div>
        </div>
        <div className="wizard-basic">
          <span className="wizard-basic-icon"><Cable size={17} /></span>
          <div>
            <strong>{t('Join nodes with cables')}</strong>
            <p>{t('Drag from the OUT dot on a node to the IN dot on another node. The cable carries the sound.')}</p>
          </div>
        </div>
        <div className="wizard-basic">
          <span className="wizard-basic-icon"><Power size={17} /></span>
          <div>
            <strong>{t('Start and stop with ON/OFF')}</strong>
            <p>{t('The switch at the top left controls the whole graph. Green ON means sound is being routed.')}</p>
          </div>
        </div>
        <div className="wizard-basic">
          <span className="wizard-basic-icon"><Volume2 size={17} /></span>
          <div>
            <strong>{t('Use the volume faders')}</strong>
            <p>{t('Each source and output has its own fader. Master volume is at the bottom right of the window.')}</p>
          </div>
        </div>
      </div>
      <div className="wizard-basic-tip">
        <SlidersHorizontal size={16} />
        <div>
          <strong>{t('More options are one right-click away')}</strong>
          <p>{t('Right-click a node or cable to rename, disconnect, bypass or remove it.')}</p>
        </div>
      </div>
      <div className="wizard-actions-inline">
        <button className="tool-button" onClick={onOpenHelp}><CircleHelp size={14} /> {t('Audio device guide')}</button>
      </div>
      <p className="wizard-muted">{t('Reopen this guide any time from Settings > Behavior > Show welcome guide.')}</p>
    </>
  );

  return (
    <Modal title={t('Welcome to Ruse')} onClose={onClose} className="wizard-modal">
      <div className="wizard-body">
        {step > 0 && (
          <div className="wizard-steps" aria-label={t('Progress')}>
            {[1, 2, 3, 4].map(value => (
              <div key={value} className={`wizard-step ${step === value ? 'active' : ''} ${step > value ? 'done' : ''}`}>
                <span className="wizard-step-number">{step > value ? <Check size={12} /> : value}</span>
                <span>{value === 1 ? t('Source') : value === 2 ? t('Output') : value === 3 ? t('Test') : t('Basics')}</span>
              </div>
            ))}
          </div>
        )}

        <div className="wizard-content">
          {step === 0 && renderIntroStep()}
          {step === 1 && renderSourceStep()}
          {step === 2 && renderOutputStep()}
          {step === 3 && renderReviewStep()}
          {step === 4 && renderBasicsStep()}
        </div>

        <div className="wizard-footer">
          <div>
            {step === 0 ? (
              <div className="wizard-intro-options">
                <label className="welcome-splash-language">
                  <span>{t('Language')}</span>
                  <select value={normalizeLanguage(settings.uiLanguage)} onChange={event => onChange({ ...settings, uiLanguage: event.target.value })}>
                    {LANGUAGES.map(language => <option key={language.id} value={language.id}>{language.label}</option>)}
                  </select>
                </label>
                <label className="welcome-splash-check">
                  <input
                    type="checkbox"
                    checked={hideNextTime}
                    onChange={event => onChange({ ...settings, showWelcomeScreen: !event.target.checked })}
                  />
                  <span>{t("Don't show this again")}</span>
                </label>
              </div>
            ) : (
              <>
                {!result && <button className="wizard-skip" onClick={onClose}>{t('Skip for now')}</button>}
                {result && step < 4 && <span className="wizard-muted">{t('You can reopen this guide any time from Settings.')}</span>}
              </>
            )}
          </div>
          <div className="wizard-footer-actions">
            {step === 0 && (
              <>
                <button className="tool-button" onClick={onClose}>{t('Explore on my own')}</button>
                <button className="primary-button" onClick={() => setStep(1)}>
                  {t('Set up my first route')} <ChevronRight size={14} />
                </button>
              </>
            )}
            {step > 1 && <button className="tool-button" onClick={() => setStep((step - 1) as Step)}><ChevronLeft size={14} /> {t('Back')}</button>}
            {step === 1 && <button className="primary-button" disabled={!sourceItem} onClick={() => setStep(2)}>{t('Continue')}</button>}
            {step === 2 && <button className="primary-button" disabled={!outputItem} onClick={() => setStep(3)}>{t('Continue')}</button>}
            {step === 3 && !result && (
              <button className={duplicateRisk ? 'warning-button' : 'primary-button'} disabled={!canCreate || creating} onClick={create}>
                {creating ? t('Creating route…') : duplicateRisk ? t('Create and start anyway') : t('Create and start')}
              </button>
            )}
            {step === 3 && result && (
              <button className={`primary-button ${hasSignal ? 'signal-ready' : ''}`} onClick={() => setStep(4)}>
                {t('Continue')}
              </button>
            )}
            {step === 4 && result && <button className="primary-button" onClick={onClose}>{t('Done')}</button>}
          </div>
        </div>
      </div>
    </Modal>
  );
}
