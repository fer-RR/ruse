import { useEffect, useState } from 'react';
import { AlertTriangle, CheckCircle2, Send } from 'lucide-react';
import { send, subscribe } from '../bridge';
import { useI18n } from '../i18n';
import { Modal } from './Dialogs';

type ReportState = 'idle' | 'sending' | 'sent' | 'error';

interface ReportResult {
  ok?: boolean;
  id?: string;
  code?: string;
  error?: string;
}

export default function SupportReportDialog({ onClose }: {
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [note, setNote] = useState('');
  const [includeDiagnostics, setIncludeDiagnostics] = useState(true);
  const [state, setState] = useState<ReportState>('idle');
  const [reportId, setReportId] = useState('');
  const [errorCode, setErrorCode] = useState('');
  const [error, setError] = useState('');

  useEffect(() => subscribe((type, payload) => {
    if (type !== 'support-report-result') return;
    const result = payload as ReportResult;
    if (result.ok) {
      setState('sent');
      setReportId(result.id ?? '');
    } else {
      setState('error');
      setErrorCode(result.code ?? 'UPLOAD');
      setError(result.error ?? t('Report upload failed.'));
    }
  }), [t]);

  const canSend = note.trim().length > 0;

  const submit = () => {
    if (state === 'sending' || !canSend) return;
    setState('sending');
    setErrorCode('');
    setError('');
    send('send-support-report', { note: note.trim(), includeDiagnostics });
  };

  return (
    <Modal title={t('Send bug report')} onClose={onClose} className="support-report-modal">
      <div className="support-report-body">
        <p>{includeDiagnostics
          ? t('This sends a ZIP containing diagnostics, sanitized logs, settings without secrets, device names, and your routes.')
          : t('Only your description and app version will be sent. No diagnostics, devices, routes, or logs are included.')}</p>
        <ul className="support-report-contents">
          {includeDiagnostics ? <>
            <li>{t('No audio, recordings, models, or clipboard contents are included.')}</li>
            <li>{t('Transcriptions, translations, text notes, and TTS contents are excluded.')}</li>
            <li>{t('Local paths, email addresses, endpoint IDs, and quoted log names are redacted.')}</li>
            <li>{t('The local LAN IP of network nodes is included; other IP addresses are redacted.')}</li>
          </> : <li>{t('No logs or system details are included.')}</li>}
        </ul>

        <label className="support-report-option">
          <input
            type="checkbox"
            checked={includeDiagnostics}
            disabled={state === 'sending' || state === 'sent'}
            onChange={event => setIncludeDiagnostics(event.target.checked)}
          />
          <span>
            <strong>{t('Include logs and diagnostics')}</strong>
            <small>{t('Recommended for troubleshooting. Turn this off to send only your description and app version.')}</small>
          </span>
        </label>

        <label className="support-report-note">
          <span>{t('Description (required)')}</span>
          <textarea
            rows={4}
            maxLength={4000}
            required
            value={note}
            placeholder={t('What went wrong?')}
            disabled={state === 'sending' || state === 'sent'}
            onChange={event => setNote(event.target.value)}
          />
          {!canSend && <small>{t('A description is required.')}</small>}
        </label>

        <div className="support-report-status" role="status" aria-live="polite">
          {state === 'sending' && <span>{t('Sending report…')}</span>}
          {state === 'sent' && (
            <span className="ok">
              <CheckCircle2 size={14} />
              {t('Report sent')}{reportId ? ` · ${t('Report ID: {id}', { id: reportId })}` : ''}
            </span>
          )}
          {state === 'error' && (
            <span className="error">
              <AlertTriangle size={14} />
              {t('Error code: {code}', { code: errorCode })} · {error}
            </span>
          )}
        </div>
      </div>
      <div className="modal-actions">
        <button type="button" className="tool-button" onClick={onClose}>
          {state === 'sent' ? t('Done') : t('Cancel')}
        </button>
        <button
          type="button"
          className="primary-button"
          disabled={!canSend || state === 'sending' || state === 'sent'}
          onClick={submit}
        >
          <Send size={13} /> {state === 'sending' ? t('Sending report…') : t('Send report')}
        </button>
      </div>
    </Modal>
  );
}
