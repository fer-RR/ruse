import { useState } from 'react';
import { Check, FolderOpen, Trash2 } from 'lucide-react';
import { useI18n } from '../i18n';
import { send } from '../bridge';
import type { OpusModelInfo, TranslatorDownload } from '../types';

function formatSize(sizeMb: number): string {
  return sizeMb >= 1000 ? `${(sizeMb / 1000).toFixed(1)} GB` : `${sizeMb} MB`;
}

function OpusTier({ model, download, pendingDelete, setPendingDelete }: {
  model: OpusModelInfo;
  download?: TranslatorDownload | null;
  pendingDelete: string | null;
  setPendingDelete: (id: string | null) => void;
}) {
  const { t } = useI18n();
  const progressId = `opus:${model.id}`;
  const isDownloading = download?.id === progressId &&
    (download.phase === 'downloading' || download.phase === 'installing');
  const isPendingDelete = pendingDelete === progressId;
  const percent = Math.max(0, Math.min(100, isDownloading ? download?.percent ?? 0 : 0));
  const error = !isDownloading && download?.id === progressId && download.phase === 'error'
    ? download.message
    : '';

  return (
    <article className={`model-tier tier-opus ${model.installed ? 'installed' : ''} ${isDownloading ? 'downloading' : ''}`}>
      <div className="model-tier-head">
        <strong>{model.name}</strong>
        <span className="model-tier-size">{formatSize(model.sizeMb)}</span>
        {model.installed && <span className="model-tier-installed" title={t('Installed')}><Check size={11} /></span>}
      </div>
      {model.license && <small className="model-tier-disk">{model.license}</small>}
      {model.installed && model.installedSize && <small className="model-tier-disk">{model.installedSize} {t('on disk')}</small>}
      {isDownloading && (
        <>
          <div className="model-progress" title={download?.message}>
            <span style={{ width: `${percent}%` }} />
          </div>
          <div className="model-tier-status">
            <span>{percent}%</span>
            <button className="small-button" onClick={() => send('translator-download-cancel')}>{t('Cancel')}</button>
          </div>
        </>
      )}
      {error && <small className="node-error">{error}</small>}
      {!isDownloading && (
        <div className="model-tier-actions">
          {isPendingDelete ? (
            <>
              <button className="small-button danger-text" onClick={() => { setPendingDelete(null); send('opus-delete', { id: model.id }); }}>{t('Delete')}</button>
              <button className="small-button" onClick={() => setPendingDelete(null)}>{t('Cancel')}</button>
            </>
          ) : model.installed ? (
            <button className="icon-button compact" title={t('Delete {name}', { name: model.name })} aria-label={t('Delete {name}', { name: model.name })} onClick={() => setPendingDelete(progressId)}><Trash2 size={12} /></button>
          ) : (
            <button className="small-button" onClick={() => send('opus-download', { id: model.id })}>{t('Download')}</button>
          )}
        </div>
      )}
    </article>
  );
}

export default function OpusModelCatalog({ models, download }: {
  models: OpusModelInfo[];
  download?: TranslatorDownload | null;
}) {
  const { t } = useI18n();
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const installed = models.filter(model => model.installed);
  const installedSize = installed.reduce((total, model) => total + model.sizeMb, 0);

  return (
    <div className="model-catalog">
      <section className="model-language" aria-label={t('OPUS-MT models')}>
        <header className="model-language-head">
          <strong>{t('OPUS-MT models')}</strong>
          <span className="model-language-meta">
            {installed.length > 0
              ? t('{count} installed', { count: installed.length }) + ` / ${formatSize(installedSize)}`
              : t('Not installed')}
          </span>
        </header>
        <div className="model-tier-grid">
          {models.map(model => (
            <OpusTier
              key={model.id}
              model={model}
              download={download}
              pendingDelete={pendingDelete}
              setPendingDelete={setPendingDelete}
            />
          ))}
        </div>
      </section>
      <div className="settings-actions">
        <button className="tool-button" onClick={() => send('translator-open-folder')}><FolderOpen size={14} /> {t('Open translation models folder')}</button>
      </div>
      <p className="settings-note">
        {t('OPUS-MT uses one small ONNX model per language pair. English pairs are listed first; other languages pivot through English. English to Japanese and Korean are not available in OPUS-MT, so use Qwen for those targets.')}
      </p>
    </div>
  );
}
