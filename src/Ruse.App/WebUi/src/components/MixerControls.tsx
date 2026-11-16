import { send } from '../bridge';
import { useI18n } from '../i18n';
import type { NodeModel, NodeTelemetry } from '../types';
import NumberField from './NumberField';

const MAX_INPUT_DELAY_MS = 500;

export default function MixerControls({ node, telemetry }: { node: NodeModel; telemetry?: NodeTelemetry }) {
  const { t } = useI18n();
  const inputs = node.mixerInputs ?? [];
  const width = node.mixerWidth ?? 1;
  const crossfade = node.mixerCrossfade ?? 0;
  const dbLeft = telemetry?.dbLeft ?? telemetry?.db;
  const dbRight = telemetry?.dbRight ?? telemetry?.db;
  const peakLeft = telemetry?.peakDbLeft ?? telemetry?.peakDb;
  const peakRight = telemetry?.peakDbRight ?? telemetry?.peakDb;
  const clipping = telemetry?.clipping === true;

  const fill = (value?: number) => {
    if (!Number.isFinite(value)) return 0;
    return Math.max(0, Math.min(100, ((value! + 60) / 60) * 100));
  };
  const formatDb = (value?: number) => value == null || value <= -59.9 ? '-\u221E' : value.toFixed(1);
  const resetMeter = (event: { stopPropagation: () => void }) => {
    event.stopPropagation();
    send('mixer-meter-reset', { id: node.id });
  };

  return (
    <div className="mixer-controls nodrag">
      {inputs.length === 0 && <div className="mixer-empty">{t('Connect audio sources to mix')}</div>}
      {inputs.map(input => (
        <div className={`mixer-input${input.muted ? ' off' : ''}`} key={input.linkId}>
          <div className="mixer-input-head">
            <span className="mixer-input-title" title={input.title}>{input.title}</span>
            <button
              className={`mixer-tag${input.muted ? ' off' : ''}`}
              title={t('Mute')}
              onClick={() => send('link-enabled', { id: input.linkId, value: input.muted })}
            >{t('M')}</button>
            <button
              className={`mixer-tag${input.solo ? ' on' : ''}`}
              title={t('Solo')}
              onClick={() => send('link-solo', { id: input.linkId, value: !input.solo })}
            >{t('S')}</button>
            <button
              className={`mixer-tag${input.busB ? ' b' : ''}`}
              title={input.busB ? t('Bus B') : t('Bus A')}
              onClick={() => send('link-bus', { id: input.linkId, value: !input.busB })}
            >{input.busB ? 'B' : 'A'}</button>
            <button
              className={`mixer-tag phase${input.inverted ? ' on' : ''}`}
              title={t('Invert polarity')}
              onClick={() => send('link-polarity', { id: input.linkId, value: !input.inverted })}
            >{'\u00D8'}</button>
          </div>
          <div className="mixer-input-row">
            <span>{t('Level')}</span>
            <input
              type="range"
              min={0}
              max={1.5}
              step={0.01}
              value={input.gain}
              onChange={event => send('link-gain', { id: input.linkId, value: Number(event.target.value) })}
            />
            <span className="mixer-value">{Math.round(input.gain * 100)}%</span>
          </div>
          <div className="mixer-input-row">
            <span>{t('Pan')}</span>
            <input
              type="range"
              min={-1}
              max={1}
              step={0.01}
              value={input.pan}
              onChange={event => send('link-pan', { id: input.linkId, value: Number(event.target.value) })}
            />
            <span className="mixer-value">{input.pan === 0 ? 'C' : input.pan < 0 ? `L${Math.round(-input.pan * 100)}` : `R${Math.round(input.pan * 100)}`}</span>
          </div>
          <div className="mixer-input-row">
            <span>{t('Delay')}</span>
            <NumberField
              value={input.delayMs ?? 0}
              min={0}
              max={MAX_INPUT_DELAY_MS}
              step={1}
              onChange={value => send('link-delay', { id: input.linkId, value })}
              ariaLabel={t('Input delay')}
            />
            <span className="mixer-unit">ms</span>
          </div>
        </div>
      ))}
      <div className="mixer-master">
        <div className="mixer-input-row">
          <span>{t('Width')}</span>
          <input
            type="range"
            min={0}
            max={2}
            step={0.01}
            value={width}
            onChange={event => send('mixer-settings', {
              id: node.id,
              width: Number(event.target.value),
              autoGain: node.mixerAutoGain === true,
              crossfade,
            })}
          />
          <span className="mixer-value">{Math.round(width * 100)}%</span>
        </div>
        <label className="parallel-toggle">
          <input
            type="checkbox"
            checked={node.mixerAutoGain === true}
            onChange={event => send('mixer-settings', {
              id: node.id,
              width,
              autoGain: event.target.checked,
              crossfade,
            })}
          />
          <span>{t('Auto gain')}</span>
        </label>
        <div className="mixer-crossover">
          <span>{t('A')}</span>
          <input
            type="range"
            min={0}
            max={1}
            step={0.01}
            value={crossfade}
            onChange={event => send('mixer-settings', {
              id: node.id,
              width,
              autoGain: node.mixerAutoGain === true,
              crossfade: Number(event.target.value),
            })}
          />
          <span>{t('B')}</span>
        </div>
      </div>
      <div
        className={`mixer-bus-meter${clipping ? ' clipping' : ''}`}
        role="button"
        tabIndex={0}
        aria-label={t('Click to reset peak hold')}
        title={t('Click to reset peak hold')}
        onClick={resetMeter}
        onKeyDown={event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            resetMeter(event);
          }
        }}
      >
        <div className="mixer-bus-meter-head">
          <span>{t('Bus output')}</span>
          <span className="mixer-clip">{clipping ? t('CLIP') : ''}</span>
        </div>
        {[{ label: 'L', db: dbLeft, peak: peakLeft }, { label: 'R', db: dbRight, peak: peakRight }].map(channel => (
          <div className="mixer-meter-row" key={channel.label}>
            <span>{channel.label}</span>
            <div className="mixer-meter-track">
              <i style={{ width: `${fill(channel.db)}%` }} />
              <b style={{ left: `${fill(channel.peak)}%` }} />
            </div>
            <span className="mixer-meter-value">{formatDb(channel.db)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
