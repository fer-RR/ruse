import { useMemo, useState } from 'react';
import { Check, FolderOpen, Trash2 } from 'lucide-react';
import { useI18n } from '../i18n';
import { send } from '../bridge';
import type { TranscriberDownload, TranscriberModelInfo } from '../types';

const TIER_ORDER: Record<string, number> = { Tiny: 0, Base: 1, Small: 2, Medium: 3, Large: 4 };

function formatModelSize(sizeMb: number): string {
  return sizeMb >= 1000 ? `${(sizeMb / 1000).toFixed(1)} GB` : `${sizeMb} MB`;
}

function formatVram(vramMb: number): string {
  return vramMb % 1024 === 0 ? `${vramMb / 1024}` : `${(vramMb / 1024).toFixed(1)}`;
}

function tierRank(tier: string): number {
  return TIER_ORDER[tier] ?? 0;
}

function largestTier(models: TranscriberModelInfo[]): string {
  return models.reduce((largest, model) =>
    tierRank(model.tier) > tierRank(largest) ? model.tier : largest, models[0]?.tier ?? 'Base');
}

function ModelTier({
  model, selected, download, activeDownload, pendingDelete, setPendingDelete, onSelectDefault,
}: {
  model: TranscriberModelInfo;
  selected: boolean;
  download?: TranscriberDownload | null;
  activeDownload: TranscriberDownload | null;
  pendingDelete: string | null;
  setPendingDelete: (id: string | null) => void;
  onSelectDefault: (id: string) => void;
}) {
  const { t } = useI18n();
  const isDownloading = activeDownload?.id === model.id;
  const isPendingDelete = pendingDelete === model.id;
  const isLarge = model.tier === 'Large' || model.tier === 'Max';
  const percent = Math.max(0, Math.min(100, activeDownload?.percent ?? 0));
  const error = !isDownloading && download?.id === model.id && download.phase === 'error'
    ? download.message
    : '';

  return (
    <article className={`model-tier tier-${model.tier.toLowerCase()} ${model.installed ? 'installed' : ''} ${selected ? 'selected' : ''} ${isDownloading ? 'downloading' : ''}`}>
      <div className="model-tier-head">
        <strong>{t(model.tier)}</strong>
        <span className="model-tier-size">{formatModelSize(model.sizeMb)}</span>
        {isLarge && <span className="model-tier-ram" title={t('Large models can need up to 16 GB of RAM')}>RAM</span>}
        {model.installed && <span className="model-tier-installed" title={t('Installed')}><Check size={11} /></span>}
      </div>
      {model.note && <small className="model-tier-note">{model.note}</small>}
      {model.vramMb ? <small className="model-tier-vram">{t('GPU: ~{size} GB VRAM', { size: formatVram(model.vramMb) })}</small> : null}
      {model.installed && model.installedSize && <small className="model-tier-disk">{model.installedSize} {t('on disk')}</small>}
      {isDownloading && (
        <>
          <div className="model-progress" title={activeDownload?.message}>
            <span style={{ width: `${percent}%` }} />
          </div>
          <div className="model-tier-status">
            <span>{activeDownload?.phase === 'installing' ? t('Installing') : `${percent}%`}</span>
            <button className="small-button" onClick={() => send('transcriber-download-cancel')}>{t('Cancel')}</button>
          </div>
        </>
      )}
      {error && <small className="node-error">{error}</small>}
      {!isDownloading && (
        <div className="model-tier-actions">
          {isPendingDelete ? (
            <>
              <button className="small-button danger-text" onClick={() => { setPendingDelete(null); send('transcriber-delete', { id: model.id }); }}>{t('Delete')}</button>
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
            <button className="small-button" disabled={!!activeDownload} onClick={() => send('transcriber-download', { id: model.id })}>{t('Download')}</button>
          )}
        </div>
      )}
    </article>
  );
}

export default function ModelCatalog({ models, selectedModel, download, onSelectDefault }: {
  models: TranscriberModelInfo[];
  selectedModel: string;
  download?: TranscriberDownload | null;
  onSelectDefault: (id: string) => void;
}) {
  const { t } = useI18n();
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const groups = useMemo(() => {
    const map = new Map<string, TranscriberModelInfo[]>();
    for (const model of models) {
      const group = map.get(model.language);
      if (group) group.push(model);
      else map.set(model.language, [model]);
    }
    return [...map.entries()];
  }, [models]);
  const activeDownload = download && (download.phase === 'downloading' || download.phase === 'installing')
    ? download
    : null;

  return (
    <div className="model-catalog">
      {groups.map(([language, entries]) => {
        const installed = entries.filter(model => model.installed).length;
        const largest = largestTier(entries);
        return (
          <section className="model-language" key={language} aria-label={language}>
            <header className="model-language-head">
              <strong>{t(language)}</strong>
              <span className="model-language-meta">
                {installed > 0 ? t('{count} installed', { count: installed }) : t('Not installed')}
                <span className="model-language-dot">{'\u00B7'}</span>
                {t('largest: {tier}', { tier: t(largest) })}
              </span>
            </header>
            <div className={`model-tier-grid ${entries.length === 1 ? 'single' : ''}`}>
              {entries.map(model => (
                <ModelTier
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
        );
      })}
      <div className="settings-actions">
        <button className="tool-button" onClick={() => send('transcriber-open-folder')}><FolderOpen size={14} /> {t('Open models folder')}</button>
      </div>
      <p className="settings-note">{t('Whisper models download once and then work offline. Larger models need more RAM and disk space; start with Base or Small. Deleting a model stops any node that uses it and falls back to another installed model.')}</p>
    </div>
  );
}
