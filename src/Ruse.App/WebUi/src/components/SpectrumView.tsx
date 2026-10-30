import { useEffect, useRef } from 'react';
import { useI18n } from '../i18n';

function formatHz(value: number | undefined): string {
  if (!value || !Number.isFinite(value)) return '';
  return value >= 1000 ? `${(value / 1000).toFixed(1)} kHz` : `${Math.round(value)} Hz`;
}

export default function SpectrumView({
  bands,
  peakHz,
  color,
  floor = -80,
  ceiling = 0,
}: {
  bands?: number[];
  peakHz?: number;
  color: string;
  floor?: number;
  ceiling?: number;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const peaks = useRef<number[]>([]);

  useEffect(() => {
    const canvas = ref.current;
    const context = canvas?.getContext('2d');
    if (!canvas || !context) return;
    const { width, height } = canvas;
    const bottom = height - 12;
    const top = 5;
    const span = Math.max(1, ceiling - floor);
    context.clearRect(0, 0, width, height);

    context.strokeStyle = 'rgba(255,255,255,0.07)';
    context.lineWidth = 1;
    for (const db of [-60, -40, -20, 0]) {
      const y = bottom - ((db - floor) / span) * (bottom - top);
      if (y < top || y > bottom) continue;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(width, y);
      context.stroke();
    }

    const values = bands?.length ? bands : new Array(64).fill(floor);
    if (peaks.current.length !== values.length) peaks.current = new Array(values.length).fill(floor);
    for (let i = 0; i < values.length; i++) {
      peaks.current[i] = Math.max(values[i], peaks.current[i] - 1.4);
    }

    const barWidth = width / values.length;
    const gradient = context.createLinearGradient(0, bottom, 0, top);
    gradient.addColorStop(0, color);
    gradient.addColorStop(0.68, '#65D6D0');
    gradient.addColorStop(1, '#E5AD51');

    context.beginPath();
    context.moveTo(0, bottom);
    for (let i = 0; i < values.length; i++) {
      const x = i * barWidth + barWidth / 2;
      const y = bottom - (Math.max(0, Math.min(1, (values[i] - floor) / span))) * (bottom - top);
      context.lineTo(x, y);
    }
    context.lineTo(width, bottom);
    context.closePath();
    context.fillStyle = gradient;
    context.globalAlpha = 0.24;
    context.fill();
    context.globalAlpha = 1;

    context.beginPath();
    for (let i = 0; i < values.length; i++) {
      const x = i * barWidth + barWidth / 2;
      const y = bottom - (Math.max(0, Math.min(1, (values[i] - floor) / span))) * (bottom - top);
      if (i === 0) context.moveTo(x, y); else context.lineTo(x, y);
    }
    context.strokeStyle = color;
    context.lineWidth = 1.4;
    context.stroke();

    context.beginPath();
    for (let i = 0; i < peaks.current.length; i++) {
      const x = i * barWidth + barWidth / 2;
      const y = bottom - (Math.max(0, Math.min(1, (peaks.current[i] - floor) / span))) * (bottom - top);
      if (i === 0) context.moveTo(x, y); else context.lineTo(x, y);
    }
    context.strokeStyle = 'rgba(255,255,255,0.42)';
    context.lineWidth = 0.8;
    context.stroke();

    context.fillStyle = 'rgba(255,255,255,0.38)';
    context.font = '9px Consolas, monospace';
    context.textBaseline = 'bottom';
    for (const [label, frequency] of [['100', 100], ['1k', 1000], ['10k', 10000], ['20k', 20000]] as const) {
      const x = (Math.log(frequency / 20) / Math.log(1000)) * width;
      context.fillText(label, Math.max(1, Math.min(width - 18, x)), height - 1);
    }

    if (!bands?.length) {
      context.fillStyle = 'rgba(255,255,255,0.22)';
      context.font = '8px system-ui, sans-serif';
      context.fillText(t('Waiting for signal'), 6, bottom - 6);
    }
  }, [bands, color, floor, ceiling, t]);

  return (
    <div className="spectrum-view">
      <canvas ref={ref} width={428} height={200} aria-label={t('Live spectrum')} />
      <div className="spectrum-meta">
        <span>{t('20 Hz - 20 kHz')}</span>
        <strong>{formatHz(peakHz)}</strong>
      </div>
    </div>
  );
}
