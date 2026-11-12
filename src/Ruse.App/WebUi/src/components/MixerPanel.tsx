import { Profiler, memo, useEffect, useMemo, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { ArrowUpDown, SlidersHorizontal, Volume2, VolumeX, X } from 'lucide-react';
import { send } from '../bridge';
import { formatGainDb, nextVolumeValue } from '../gain';
import { useI18n } from '../i18n';
import { useGlobalTelemetry, useNodeTelemetry, useUiTelemetry } from '../telemetry';
import type { LinkModel, NodeModel } from '../types';

const MIN_DB = -60;
const EMPTY_INSERTS: NodeModel[] = [];

function clampDb(value: number | undefined, fallbackLevel: number | undefined): number {
  const raw = value ?? (fallbackLevel && fallbackLevel > 0 ? 20 * Math.log10(fallbackLevel) : MIN_DB);
  return Number.isFinite(raw) ? Math.max(MIN_DB, Math.min(0, raw)) : MIN_DB;
}

function dbPercent(db: number): number {
  return Math.max(0, Math.min(100, ((db - MIN_DB) / -MIN_DB) * 100));
}

function formatDb(db: number): string {
  return db <= MIN_DB + 0.05 ? '-\u221E' : db.toFixed(1);
}

function MeterBars({ left, right, vertical, clipping, clean = false }: {
  left: number;
  right: number;
  vertical: boolean;
  clipping: boolean;
  clean?: boolean;
}) {
  return (
    <div className={`mixer-meter stereo ${clipping ? 'clipping' : ''} ${clean ? 'clean' : ''}`}>
      <div className="mixer-meter-channel">
        <span style={vertical ? { height: `${dbPercent(left)}%` } : { width: `${dbPercent(left)}%` }} />
        <small>L</small>
      </div>
      <div className="mixer-meter-channel">
        <span style={vertical ? { height: `${dbPercent(right)}%` } : { width: `${dbPercent(right)}%` }} />
        <small>R</small>
      </div>
    </div>
  );
}

function buildSourceInserts(sources: NodeModel[], modifiers: NodeModel[], links: LinkModel[]): Map<string, NodeModel[]> {
  const modifierById = new Map(modifiers.map(modifier => [modifier.id, modifier]));
  const outgoing = new Map<string, string[]>();
  for (const link of links) {
    if (link.kind !== 'source-modifier' && link.kind !== 'modifier-modifier') continue;
    const list = outgoing.get(link.from) ?? [];
    list.push(link.to);
    outgoing.set(link.from, list);
  }

  const result = new Map<string, NodeModel[]>();
  for (const source of sources) {
    const inserts: NodeModel[] = [];
    const seen = new Set<string>();
    const walk = (id: string) => {
      for (const next of outgoing.get(id) ?? []) {
        const modifier = modifierById.get(next);
        if (!modifier || seen.has(modifier.id)) continue;
        seen.add(modifier.id);
        inserts.push(modifier);
        walk(modifier.id);
      }
    };
    walk(source.id);
    result.set(source.id, inserts);
  }

  return result;
}

function buildOutputInserts(outputs: NodeModel[], modifiers: NodeModel[], links: LinkModel[]): Map<string, NodeModel[]> {
  const modifierById = new Map(modifiers.map(modifier => [modifier.id, modifier]));
  const incoming = new Map<string, string[]>();
  for (const link of links) {
    if (link.kind !== 'modifier-output' && link.kind !== 'modifier-modifier') continue;
    const list = incoming.get(link.to) ?? [];
    list.push(link.from);
    incoming.set(link.to, list);
  }

  const result = new Map<string, NodeModel[]>();
  for (const output of outputs) {
    const inserts: NodeModel[] = [];
    const seen = new Set<string>();
    const walk = (id: string) => {
      for (const previous of incoming.get(id) ?? []) {
        const modifier = modifierById.get(previous);
        if (!modifier || seen.has(modifier.id)) continue;
        seen.add(modifier.id);
        walk(modifier.id);
        inserts.push(modifier);
      }
    };
    walk(output.id);
    result.set(output.id, inserts);
  }

  return result;
}

function terminalModifier(inserts: NodeModel[], links: LinkModel[]): NodeModel | undefined {
  const terminals = inserts.filter(modifier =>
    !links.some(link => link.kind === 'modifier-modifier' && link.from === modifier.id));
  return terminals.length === 1 ? terminals[0] : undefined;
}

function SendRowImpl({ source, output, inserts, links }: {
  source: NodeModel;
  output: NodeModel;
  inserts: NodeModel[];
  links: LinkModel[];
}) {
  const { t } = useI18n();
  const postModifier = terminalModifier(inserts, links);
  const directLink = links.find(link => link.kind === 'direct' && link.from === source.id && link.to === output.id);
  const postLink = postModifier
    ? links.find(link => link.kind === 'modifier-output' && link.from === postModifier.id && link.to === output.id)
    : undefined;
  const active = directLink ?? postLink;
  const [mode, setMode] = useState<'pre' | 'post'>(directLink ? 'pre' : postLink ? 'post' : 'pre');
  const [gain, setGain] = useState(active?.gain ?? 1);

  useEffect(() => {
    if (directLink) setMode('pre');
    else if (postLink) setMode('post');
  }, [directLink?.id, postLink?.id]);

  useEffect(() => {
    setGain(active?.gain ?? 1);
  }, [active?.id, active?.gain]);

  const enabled = !!active && active.enabled !== false;

  const connect = (nextMode: 'pre' | 'post', nextGain: number, nextEnabled: boolean) => {
    const from = nextMode === 'post' ? postModifier : source;
    if (!from) return;
    send('connect', {
      from: from.id,
      to: output.id,
      gain: nextGain,
      enabled: nextEnabled,
    });
  };

  const setSendMode = (next: 'pre' | 'post') => {
    setMode(next);
    if (!active) return;
    send('disconnect', { id: active.id });
    connect(next, gain, enabled);
  };

  const toggle = () => {
    if (active) {
      send('link-enabled', { id: active.id, value: !enabled });
    } else {
      connect(mode, gain, true);
    }
  };

  const commitGain = () => {
    if (active) send('link-gain', { id: active.id, value: gain });
  };

  return (
    <div className={`mixer-send ${enabled ? '' : 'off'}`}>
      <label className="mixer-send-name">
        <input type="checkbox" checked={enabled} onChange={toggle} />
        <span title={output.title}>{output.title}</span>
      </label>
      <div className="mixer-send-row">
        {postModifier ? (
          <select
            value={mode}
            aria-label={t('Send point')}
            onChange={event => setSendMode(event.target.value === 'post' ? 'post' : 'pre')}
          >
            <option value="pre">{t('Pre')}</option>
            <option value="post">{t('Post')}</option>
          </select>
        ) : (
          <span className="mixer-send-mode">{t('Pre')}</span>
        )}
        <span className="volume-slider">
          <input
            className="mixer-send-gain"
            type="range"
            min={0}
            max={1.5}
            step={0.01}
            value={gain}
            aria-label={t('{name} volume', { name: output.title })}
            aria-valuetext={formatGainDb(gain)}
            onChange={event => setGain(Number(event.target.value))}
            onDoubleClick={() => setGain(1)}
            onKeyDown={event => {
              const next = nextVolumeValue(event, gain);
              if (next !== null) {
                event.preventDefault();
                setGain(next);
              }
            }}
            onPointerUp={commitGain}
            onKeyUp={commitGain}
          />
          <small className="volume-db">{formatGainDb(gain)}</small>
        </span>
        <span>{Math.round(gain * 100)}%</span>
      </div>
    </div>
  );
}

const SendRow = memo(SendRowImpl);

function MixerStrip({ node, outputs, links, inserts, vertical, onFocusNode }: {
  node: NodeModel;
  outputs: NodeModel[];
  links: LinkModel[];
  inserts: NodeModel[];
  vertical: boolean;
  onFocusNode: (id: string) => void;
}) {
  const { t } = useI18n();
  const telemetry = useNodeTelemetry(node.id);
  const [volume, setVolume] = useState(node.volume);
  useEffect(() => setVolume(node.volume), [node.volume]);

  const level = Math.max(0, Math.min(1, telemetry?.level ?? 0));
  const dbLeft = clampDb(telemetry?.meterDbLeft, telemetry?.levelLeft ?? level);
  const dbRight = clampDb(telemetry?.meterDbRight, telemetry?.levelRight ?? level);
  const recording = node.isRecorder && telemetry?.recordingStatus
    ? `${telemetry.recordingStatus}${telemetry.recordingSeconds ? ` ${Math.floor(telemetry.recordingSeconds / 60)}:${String(Math.floor(telemetry.recordingSeconds % 60)).padStart(2, '0')}` : ''}`
    : '';

  return (
    <div className={`mixer-strip mixer-${node.kind} ${vertical ? 'vertical' : ''} ${node.muted ? 'muted' : ''} ${node.solo ? 'soloed' : ''} ${telemetry?.clipping ? 'clipping' : ''}`}>
      <div className="mixer-strip-head">
        <input
          className="mixer-color"
          type="color"
          value={node.color ?? node.accent}
          title={t('Channel color')}
          aria-label={t('{name} color', { name: node.title })}
          onChange={event => send('node-color', { id: node.id, color: event.target.value })}
        />
        <span
          className="mixer-strip-name"
          role="button"
          tabIndex={0}
          title={node.title}
          onClick={() => onFocusNode(node.id)}
          onDoubleClick={() => window.dispatchEvent(new CustomEvent('ruse-rename-node', { detail: { id: node.id, title: node.title } }))}
          onKeyDown={event => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              onFocusNode(node.id);
            }
          }}
        >{node.title}</span>
        <button
          className={`mixer-solo ${node.solo ? 'active' : ''}`}
          title={node.solo ? t('Unsolo') : t('Solo')}
          aria-label={node.solo ? t('Unsolo') : t('Solo')}
          onClick={() => send('solo', { id: node.id, value: !node.solo })}
        >S</button>
        <button
          className={`mixer-mute ${node.muted ? 'active' : ''}`}
          title={node.muted ? t('Unmute') : t('Mute')}
          aria-label={node.muted ? t('Unmute') : t('Mute')}
          onClick={() => send('mute', { id: node.id, value: !node.muted })}
        >
          {node.muted ? <VolumeX size={14} /> : <Volume2 size={14} />}
        </button>
      </div>
      <MeterBars left={dbLeft} right={dbRight} vertical={vertical} clipping={!!telemetry?.clipping} />
      <div className="mixer-db-readout"><span>{formatDb(dbLeft)}</span><span>{formatDb(dbRight)}</span><span>dB</span></div>
      <div className="mixer-fader-wrap">
        <input
          className="mixer-fader"
          type="range"
          min={0}
          max={1.5}
          step={0.01}
          value={volume}
          aria-label={t('{name} volume', { name: node.title })}
          aria-valuetext={formatGainDb(volume)}
          onChange={event => {
            const next = Number(event.target.value);
            setVolume(next);
            send('gain', { id: node.id, value: next });
          }}
          onDoubleClick={() => {
            setVolume(1);
            send('gain', { id: node.id, value: 1 });
          }}
          onKeyDown={event => {
            const next = nextVolumeValue(event, volume);
            if (next !== null) {
              event.preventDefault();
              setVolume(next);
              send('gain', { id: node.id, value: next });
            }
          }}
        />
      </div>
      <div className="fader-scale" aria-hidden="true">
        <span>{'\u2212\u221E'}</span><span>{'\u22126'}</span><span>0</span><span>+3.5</span>
      </div>
      <small className="volume-db">{formatGainDb(volume)}</small>
      <div className="mixer-strip-foot">
        <span>{Math.round(volume * 100)}%</span>
        {recording && <span className="mixer-recording">{recording}</span>}
      </div>
      {inserts.length > 0 && (
        <div className="mixer-inserts">
          <span className="mixer-inserts-label">{t('Processing')}</span>
          <div className="mixer-insert-list">
            {inserts.map(modifier => (
              <button
                key={modifier.id}
                type="button"
                className={`mixer-insert ${modifier.enabled === false ? 'bypassed' : ''}`}
                title={modifier.enabled === false ? t('Enable effect') : t('Bypass effect')}
                onClick={() => send('effect-enabled', { id: modifier.id, value: modifier.enabled === false })}
              >
                {modifier.title}
              </button>
            ))}
          </div>
        </div>
      )}
      {node.kind === 'source' && outputs.length > 0 && (
        <details className="mixer-sends">
          <summary>{t('Sends')}</summary>
          {outputs.map(output => (
            <SendRow
              key={output.id}
              source={node}
              output={output}
              inserts={inserts}
              links={links}
            />
          ))}
        </details>
      )}
    </div>
  );
}

function MasterMeter({ outputIds, vertical }: { outputIds: string[]; vertical: boolean }) {
  const telemetry = useGlobalTelemetry();
  let left = MIN_DB;
  let right = MIN_DB;
  for (const id of outputIds) {
    const meter = telemetry.meters[id];
    if (!meter) continue;
    left = Math.max(left, clampDb(meter.meterDbLeft, meter.levelLeft ?? meter.level));
    right = Math.max(right, clampDb(meter.meterDbRight, meter.levelRight ?? meter.level));
  }

  return (
    <>
      <MeterBars left={left} right={right} vertical={vertical} clipping={telemetry.clipping} clean />
      <div className="mixer-db-readout"><span>{formatDb(left)}</span><span>{formatDb(right)}</span><span>dB</span></div>
    </>
  );
}

function MasterStrip({ outputs, vertical }: { outputs: NodeModel[]; vertical: boolean }) {
  const { t } = useI18n();
  const ui = useUiTelemetry();
  const [volume, setVolume] = useState(ui.masterGain);
  useEffect(() => setVolume(ui.masterGain), [ui.masterGain]);

  return (
    <div className={`mixer-strip mixer-output mixer-master ${vertical ? 'vertical' : ''} ${ui.masterMuted ? 'muted' : ''}`}>
      <div className="mixer-strip-head">
        <span className="mixer-strip-name" title={t('Master')}>{t('Master')}</span>
        <button
          className={`mixer-mute ${ui.masterMuted ? 'active' : ''}`}
          title={ui.masterMuted ? t('Unmute all outputs') : t('Mute all outputs')}
          aria-label={ui.masterMuted ? t('Unmute all outputs') : t('Mute all outputs')}
          onClick={() => send('master-mute', { value: !ui.masterMuted })}
        >
          {ui.masterMuted ? <VolumeX size={14} /> : <Volume2 size={14} />}
        </button>
      </div>
      <MasterMeter outputIds={outputs.map(output => output.id)} vertical={vertical} />
      <div className="mixer-fader-wrap">
        <input
          className="mixer-fader"
          type="range"
          min={0}
          max={1.5}
          step={0.01}
          value={volume}
          aria-label={t('Master')}
          aria-valuetext={formatGainDb(volume)}
          onChange={event => {
            const next = Number(event.target.value);
            setVolume(next);
            send('master-gain', { value: next });
          }}
          onDoubleClick={() => {
            setVolume(1);
            send('master-gain', { value: 1 });
          }}
          onKeyDown={event => {
            const next = nextVolumeValue(event, volume);
            if (next !== null) {
              event.preventDefault();
              setVolume(next);
              send('master-gain', { value: next });
            }
          }}
        />
      </div>
      <div className="fader-scale" aria-hidden="true">
        <span>{'\u2212\u221E'}</span><span>{'\u22126'}</span><span>0</span><span>+3.5</span>
      </div>
      <small className="volume-db">{formatGainDb(volume)}</small>
      <div className="mixer-strip-foot"><span>{Math.round(volume * 100)}%</span><span>{t('Master')}</span></div>
    </div>
  );
}

function mixGroups(nodes: NodeModel[]) {
  const groups = new Map<string, NodeModel[]>();
  for (const node of nodes) {
    const color = node.color ?? 'default';
    const list = groups.get(color) ?? [];
    list.push(node);
    groups.set(color, list);
  }
  return [...groups.entries()];
}

function MixerPanel({ sources, outputs, modifiers, links, onClose, onResizeStart, onFocusNode }: {
  sources: NodeModel[];
  outputs: NodeModel[];
  modifiers: NodeModel[];
  links: LinkModel[];
  onClose: () => void;
  onResizeStart: (event: ReactPointerEvent<HTMLDivElement>) => void;
  onFocusNode: (id: string) => void;
}) {
  const { t } = useI18n();
  const [vertical, setVertical] = useState(() => localStorage.getItem('ruse.mixerVertical') === '1');
  const perfRef = useRef<HTMLSpanElement>(null);
  const perfAt = useRef(0);

  useEffect(() => {
    localStorage.setItem('ruse.mixerVertical', vertical ? '1' : '0');
  }, [vertical]);

  const sourceInserts = useMemo(
    () => buildSourceInserts(sources, modifiers, links),
    [sources, modifiers, links],
  );
  const outputInserts = useMemo(
    () => buildOutputInserts(outputs, modifiers, links),
    [outputs, modifiers, links],
  );

  return (
    <Profiler
      id="ruse-mixer"
      onRender={(_id, _phase, actualDuration) => {
        const now = performance.now();
        if (now - perfAt.current < 500) return;
        perfAt.current = now;
        if (perfRef.current) perfRef.current.textContent = `${actualDuration.toFixed(2)} ms`;
      }}
    >
      <aside className={`mixer-panel ${vertical ? 'vertical' : ''}`} aria-label={t('Mixer')}>
        <div className="mixer-resizer" role="separator" aria-label={t('Resize mixer')} onPointerDown={onResizeStart} />
        <div className="mixer-head">
          <span className="mixer-title"><SlidersHorizontal size={14} /><span className="eyebrow">{t('Mixer')}</span></span>
          <span className="mixer-perf" ref={perfRef} title={t('Performance')}>{'\u2014'}</span>
          <button
            className="icon-button compact"
            title={vertical ? t('Make horizontal') : t('Make vertical')}
            aria-label={vertical ? t('Make horizontal') : t('Make vertical')}
            onClick={() => setVertical(value => !value)}
          >
            <ArrowUpDown size={14} />
          </button>
          <button className="icon-button compact" title={t('Hide mixer')} aria-label={t('Hide mixer')} onClick={onClose}>
            <X size={14} />
          </button>
        </div>
        <div className="mixer-scroll">
          <section className="mixer-section">
            <div className="mixer-section-head"><span>{t('Sources')}</span><span>{sources.length}</span></div>
            {sources.length === 0
              ? <p className="mixer-empty">{t('No sources on the canvas.')}</p>
              : mixGroups(sources).map(([color, items]) => (
                <details className="mixer-group" open key={color}>
                  <summary>
                    <i style={{ background: color === 'default' ? 'var(--text-3)' : color }} />
                    <span>{color === 'default' ? t('Ungrouped') : color}</span>
                    <span>{items.length}</span>
                  </summary>
                  <div className="mixer-strips">
                    {items.map(node => <MixerStrip
                      key={node.id}
                      node={node}
                      outputs={outputs}
                      links={links}
                      inserts={sourceInserts.get(node.id) ?? EMPTY_INSERTS}
                      vertical={vertical}
                      onFocusNode={onFocusNode}
                    />)}
                  </div>
                </details>
              ))}
          </section>
          <section className="mixer-section">
            <div className="mixer-section-head"><span>{t('Outputs')}</span><span>{outputs.length}</span></div>
            {outputs.length === 0
              ? <p className="mixer-empty">{t('No outputs on the canvas.')}</p>
              : mixGroups(outputs).map(([color, items]) => (
                <details className="mixer-group" open key={color}>
                  <summary>
                    <i style={{ background: color === 'default' ? 'var(--text-3)' : color }} />
                    <span>{color === 'default' ? t('Ungrouped') : color}</span>
                    <span>{items.length}</span>
                  </summary>
                  <div className="mixer-strips">
                    {items.map(node => <MixerStrip
                      key={node.id}
                      node={node}
                      outputs={outputs}
                      links={links}
                      inserts={outputInserts.get(node.id) ?? EMPTY_INSERTS}
                      vertical={vertical}
                      onFocusNode={onFocusNode}
                    />)}
                  </div>
                </details>
              ))}
          </section>
          <section className="mixer-section mixer-master-section">
            <div className="mixer-section-head"><span>{t('Master')}</span></div>
            <div className="mixer-strips">
              <MasterStrip outputs={outputs} vertical={vertical} />
            </div>
          </section>
        </div>
      </aside>
    </Profiler>
  );
}

export default memo(MixerPanel);
