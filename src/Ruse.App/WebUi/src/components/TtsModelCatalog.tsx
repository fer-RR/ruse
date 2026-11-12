import { useState } from 'react';
import { Check, FolderOpen, Trash2 } from 'lucide-react';
import { useI18n } from '../i18n';
import { send } from '../bridge';
import type { TtsDownload, TtsModelInfo } from '../types';

function formatSize(sizeMb: number): string {
  return sizeMb >= 1000 ? `${(sizeMb / 1000).toFixed(1)} GB` : `${sizeMb} MB`;
}

function TtsTier({
  model, selected, download, activeDownload, pendingDelete, setPendingDelete, onSelectDefault,
}: {
  model: TtsModelInfo;
  selected: boolean;
  download?: TtsDownload | null;
  activeDownload: TtsDownload | null;
  pendingDelete: string | null;
  setPendingDelete: (id: string | null) => void;
  onSelectDefault: (id: string) => void;
}) {
  const { t } = useI18n();
  const isDownloading = activeDownload?.id === model.id;
  const isPendingDelete = pendingDelete === model.id;
  const percent = Math.max(0, Math.min(100, activeDownload?.percent ?? 0));
  const error = !isDownloading && download?.id === model.id && download.phase === 'error'
    ? download.message
    : '';

  return (
    <article className={`model-tier tier-tts ${model.installed ? 'installed' : ''} ${selected ? 'selected' : ''} ${isDownloading ? 'downloading' : ''}`} aria-busy={isDownloading || undefined}>
      <div className="model-tier-head">
        <strong>{model.name}</strong>
        <span className="model-tier-size">{formatSize(model.sizeMb)}</span>
        {model.installed && <span className="model-tier-installed" title={t('Installed')}><Check size={11} /></span>}
      </div>
      <small className="model-tier-note">{model.languages}</small>
      {model.note && <small className="model-tier-disk">{t(model.note)}</small>}
      {model.license && <small className="model-tier-disk">{model.license}</small>}
      {model.installed && model.installedSize && <small className="model-tier-disk">{model.installedSize} {t('on disk')}</small>}
      {isDownloading && (
        <>
          <div className="model-progress" title={activeDownload?.message} role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}>
            <span style={{ width: `${percent}%` }} />
          </div>
          <div className="model-tier-status">
            <span>{activeDownload?.phase === 'installing' ? t('Installing') : `${percent}%`}</span>
            <button className="small-button" onClick={() => send('tts-download-cancel')}>{t('Cancel')}</button>
          </div>
        </>
      )}
      {error && <small className="node-error">{error}</small>}
      {!isDownloading && (
        <div className="model-tier-actions">
          {isPendingDelete ? (
            <>
              <button className="small-button danger-text" onClick={() => { setPendingDelete(null); send('tts-delete', { id: model.id }); }}>{t('Delete')}</button>
              <button className="small-button" onClick={() => setPendingDelete(null)}>{t('Cancel')}</button>
            </>
          ) : model.installed ? (
            <>
              {selected
                ? <span className="model-default">{t('Default')}</span>
                : <button className="small-button" onClick={() => onSelectDefault(model.id)}>{t('Use')}</button>}
              <button className="icon-button compact" title={t('Delete {name}', { name: model.name })} aria-label={t('Delete {name}', { name: model.name })} onClick={() => setPendingDelete(model.id)}><Trash2 size={12} /></button>
            </>
          ) : (
            <button className="small-button" disabled={!!activeDownload} onClick={() => send('tts-download', { id: model.id })}>{t('Download')}</button>
          )}
        </div>
      )}
    </article>
  );
}

export default function TtsModelCatalog({ models, selectedModel, download, onSelectDefault }: {
  models: TtsModelInfo[];
  selectedModel: string;
  download?: TtsDownload | null;
  onSelectDefault: (id: string) => void;
}) {
  const { t } = useI18n();
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const activeDownload = download && (download.phase === 'downloading' || download.phase === 'installing')
    ? download
    : null;
  const installed = models.filter(model => model.installed).length;

  return (
    <div className="model-catalog">
      <section className="model-language" aria-label={t('Offline text to speech')}>
        <header className="model-language-head">
          <strong>{t('Offline text to speech')}</strong>
          <span className="model-language-meta">
            {installed > 0 ? t('{count} installed', { count: installed }) : t('Not installed')}
          </span>
        </header>
        <div className={`model-tier-grid ${models.length === 1 ? 'single' : ''}`}>
          {models.map(model => (
            <TtsTier
              key={model.id}
              model={model}
              selected={model.id === selectedModel}
              download={download}
              activeDownload={activeDownload}
              pendingDelete={pendingDelete}
              setPendingDelete={setPendingDelete}
              onSelectDefault={onSelectDefault}
            />
          ))}
        </div>
      </section>
      <div className="settings-actions">
        <button className="tool-button" onClick={() => send('tts-open-folder')}><FolderOpen size={14} /> {t('Open TTS models folder')}</button>
      </div>
      <p className="settings-note">
        {t('Text-to-speech models download once and then work offline. Supertonic 3 supports many languages; Kokoro provides English and Chinese voices.')}
      </p>
    </div>
  );
}
