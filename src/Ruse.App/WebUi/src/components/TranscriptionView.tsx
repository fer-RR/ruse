import { useMemo, useState } from 'react';
import { Check, Clipboard, Download, RotateCcw, Trash2 } from 'lucide-react';
import { send } from '../bridge';
import { useI18n } from '../i18n';

function formatDuration(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(whole / 60);
  return `${minutes}:${String(whole % 60).padStart(2, '0')}`;
}

function stateClass(status: string | undefined, error: string | undefined, hasAudio: boolean | undefined): string {
  if (error) return 'error';
  if (!status) return 'idle';
  if (status === 'Transcribing') return 'active';
  if (status === 'Listening') return 'listening';
  if (status === 'No speech model installed' || status.includes('error') || status.includes('Error')) return 'error';
  if (hasAudio) return 'active';
  return 'idle';
}

export default function TranscriptionView({
  id, status, transcript, partial, error, level, hasAudio, elapsed,
}: {
  id: string;
  status?: string;
  transcript?: string;
  partial?: string;
  error?: string;
  level?: number;
  hasAudio?: boolean;
  elapsed?: number;
}) {
  const { t } = useI18n();
  const [copied, setCopied] = useState(false);
  const text = useMemo(() => [transcript, partial].filter(Boolean).join(' ').trim(), [transcript, partial]);
  const hasText = Boolean(transcript?.trim() || partial?.trim());
  const meter = Math.min(1, Math.max(0, Math.pow(level ?? 0, 0.65)));
  const tone = stateClass(status, error, hasAudio);

  const copy = async () => {
    if (!hasText) return;
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
      send('transcriber-export', { id });
    }
  };

  return (
    <div className={`transcription-view ${tone}`}>
      <div className="transcription-head">
        <span className="transcription-status"><span className="status-dot" />{t(status ?? 'Waiting for audio')}</span>
        {elapsed ? <span className="transcription-clock">{formatDuration(elapsed)}</span> : null}
      </div>
      <div className="transcription-level" title={t('Input level')} aria-label={t('Transcription input level {level}%', { level: Math.round(meter * 100) })}>
        <span style={{ width: `${Math.round(meter * 100)}%` }} />
      </div>
      <div className="transcription-text" aria-live="polite">
        {transcript
          ? transcript
          : <span className="transcription-placeholder">
              {error
                ? t('Model unavailable. See the error below.')
                : status === 'No speech model installed'
                  ? t('Install a speech model in Settings, then press Retry.')
                  : hasAudio
                    ? t('Listening for speech…')
                    : t('Waiting for audio. Connect an input to this node.')}
            </span>}
        {partial && <span className="transcription-partial"> {partial}</span>}
      </div>
      {error && <div className="node-error" role="alert">{error}</div>}
      <div className="transcription-actions nodrag">
        <button className="small-button" disabled={!hasText} onClick={() => send('transcriber-clear', { id })}>
          <Trash2 size={11} /> {t('Clear')}
        </button>
        <button className="small-button" disabled={!hasText} onClick={copy}>
          {copied ? <Check size={11} /> : <Clipboard size={11} />} {copied ? t('Copied') : t('Copy')}
        </button>
        <button className="small-button" disabled={!transcript?.trim()} onClick={() => send('transcriber-export', { id })}>
          <Download size={11} /> {t('Export')}
        </button>
        {(error || status === 'No speech model installed') && (
          <button className="small-button" onClick={() => send('transcriber-restart', { id })}>
            <RotateCcw size={11} /> {t('Retry')}
          </button>
        )}
      </div>
    </div>
  );
}
