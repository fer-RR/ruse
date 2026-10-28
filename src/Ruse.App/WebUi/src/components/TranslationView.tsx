import { useMemo, useState } from 'react';
import { Check, Clipboard, Download, RotateCcw, Trash2 } from 'lucide-react';
import { send } from '../bridge';
import { useI18n } from '../i18n';
import { languageLabel, translationTarget } from '../languages';

function formatDuration(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, '0')}`;
}

function stateClass(status: string | undefined, error: string | undefined, hasAudio: boolean | undefined): string {
  if (error) return 'error';
  if (!status) return 'idle';
  if (status === 'Transcribing' || status === 'Translating') return 'active';
  if (status === 'Listening') return 'listening';
  if (status.includes('error') || status.includes('Error') || status.includes('No translation model') || status === 'No speech model installed') return 'error';
  if (hasAudio) return 'active';
  return 'idle';
}

export default function TranslationView({
  id, status, source, sourcePartial, translated, translatedPartial, error, level, hasAudio, elapsed, detected, target,
}: {
  id: string;
  status?: string;
  source?: string;
  sourcePartial?: string;
  translated?: string;
  translatedPartial?: string;
  error?: string;
  level?: number;
  hasAudio?: boolean;
  elapsed?: number;
  detected?: string;
  target?: string;
}) {
  const { t } = useI18n();
  const [copied, setCopied] = useState(false);
  const text = useMemo(() => [translated, translatedPartial].filter(Boolean).join(' ').trim(), [translated, translatedPartial]);
  const sourceText = useMemo(() => [source, sourcePartial].filter(Boolean).join(' ').trim(), [source, sourcePartial]);
  const hasText = Boolean(text || sourceText);
  const meter = Math.min(1, Math.max(0, Math.pow(level ?? 0, 0.65)));
  const tone = stateClass(status, error, hasAudio);
  const targetLabel = translationTarget(target).label;
  const detectedLabel = detected ? languageLabel(detected) : '';

  const copy = async () => {
    if (!text) return;
    try {
      if (navigator.clipboard) {
        await navigator.clipboard.writeText(text);
      } else {
        const area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        document.execCommand('copy');
        area.remove();
      }
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1400);
    } catch {
      send('translator-export', { id });
    }
  };

  return (
    <div className={`transcription-view translation-view ${tone}`}>
      <div className="transcription-head">
        <span className="transcription-status"><span className="status-dot" />{t(status ?? 'Waiting for audio')}</span>
        <span className="translation-flow">
          {detectedLabel ? `${detectedLabel} → ${targetLabel}` : targetLabel}
        </span>
        {elapsed ? <span className="transcription-clock">{formatDuration(elapsed)}</span> : null}
      </div>
      <div className="transcription-level" title={t('Input level')} aria-label={t('Transcription input level {level}%', { level: Math.round(meter * 100) })}>
        <span style={{ width: `${Math.round(meter * 100)}%` }} />
      </div>
      <div className="translation-pane">
        <span className="translation-label">{t('Original')}</span>
        <div className="transcription-text" aria-live="polite">
          {sourceText || <span className="transcription-placeholder">
            {error
              ? t('The original text is unavailable. See the error below.')
              : hasAudio ? t('Listening for speech…') : t('Waiting for audio. Connect an input to this node.')}
          </span>}
          {sourcePartial && <span className="transcription-partial"> {sourcePartial}</span>}
        </div>
      </div>
      <div className="translation-pane translated">
        <span className="translation-label">{targetLabel}</span>
        <div className="transcription-text" aria-live="polite">
          {text || <span className="transcription-placeholder">
            {status === 'No translation model installed'
              ? t('Install a translation model in Settings, then press Retry.')
              : status === 'No speech model installed'
                ? t('Install a speech model in Settings, then press Retry.')
                : t('Waiting for a phrase to translate.')}
          </span>}
          {translatedPartial && <span className="transcription-partial"> {translatedPartial}</span>}
        </div>
      </div>
      {error && <div className="node-error" role="alert">{error}</div>}
      <div className="transcription-actions nodrag">
        <button className="small-button" disabled={!hasText} onClick={() => send('translator-clear', { id })}>
          <Trash2 size={11} /> {t('Clear')}
        </button>
        <button className="small-button" disabled={!text} onClick={copy}>
          {copied ? <Check size={11} /> : <Clipboard size={11} />} {copied ? t('Copied') : t('Copy')}
        </button>
        <button className="small-button" disabled={!hasText} onClick={() => send('translator-export', { id })}>
          <Download size={11} /> {t('Export')}
        </button>
        {(error || status === 'No speech model installed' || status === 'No translation model installed') && (
          <button className="small-button" onClick={() => send('translator-restart', { id })}>
            <RotateCcw size={11} /> {t('Retry')}
          </button>
        )}
      </div>
    </div>
  );
}
