import { AppWindow, Check, RefreshCw } from 'lucide-react';
import { send } from '../bridge';
import { useI18n } from '../i18n';
import type { CatalogItem } from '../types';
import { Modal } from './Dialogs';

export default function ProcessPickerDialog({ nodeId, nodeTitle, processName, processId, apps, onClose }: {
  nodeId: string;
  nodeTitle: string;
  processName?: string;
  processId?: number;
  apps: CatalogItem[];
  onClose: () => void;
}) {
  const { t } = useI18n();
  const name = (processName ?? '').toLowerCase();
  const matches = apps
    .filter(item => !name || item.processName?.toLowerCase() === name || (!item.processName && item.name.toLowerCase() === name))
    .sort((a, b) => Number(Boolean(b.active)) - Number(Boolean(a.active)) || a.name.localeCompare(b.name));

  const pick = (item: CatalogItem) => {
    if (item.processId == null) return;
    send('retarget-app', { id: nodeId, processId: item.processId, processName: item.processName ?? item.name });
    onClose();
  };

  return (
    <Modal title={t('Choose process')} onClose={onClose} className="small-modal process-picker-modal">
      <div className="process-picker-body">
        <p className="modal-copy">{t('Choose which process {name} should follow.', { name: nodeTitle })}</p>
        <div className="process-picker-head">
          <span className="eyebrow">{t('Application')}</span>
          <button type="button" className="icon-button compact" title={t('Refresh applications')} aria-label={t('Refresh applications')} onClick={() => send('refresh')}><RefreshCw size={14} /></button>
        </div>
        {matches.length === 0
          ? <p className="first-route-empty">{t('No applications found. Start playback, then refresh.')}</p>
          : <div className="first-route-list">
              {matches.map(item => (
                <button type="button" key={item.id} className={`first-route-choice ${item.processId === processId ? 'selected' : ''}`} onClick={() => pick(item)}>
                  <span className="first-route-choice-icon"><AppWindow size={16} /></span>
                  <span><strong>{item.name}</strong><small>PID {item.processId}{item.active ? ` · ${t('Playing now')}` : ''}</small></span>
                  {item.processId === processId && <Check size={16} />}
                </button>
              ))}
            </div>}
      </div>
      <div className="modal-actions">
        <button type="button" className="tool-button" onClick={onClose}>{t('Cancel')}</button>
      </div>
    </Modal>
  );
}
