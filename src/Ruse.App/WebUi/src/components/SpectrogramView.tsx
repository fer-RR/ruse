import { useEffect, useRef } from 'react';
import { useI18n } from '../i18n';

const BANDS = 64;

function formatHz(value: number | undefined): string {
  if (!value || !Number.isFinite(value)) return '';
  return value >= 1000 ? `${(value / 1000).toFixed(1)} kHz` : `${Math.round(value)} Hz`;
}

function color(value: number): [number, number, number] {
  const t = Math.max(0, Math.min(1, value / 255));
  if (t < 0.34) {
    const k = t / 0.34;
    return [10 + k * 35, 22 + k * 150, 36 + k * 160];
  }

  if (t < 0.68) {
    const k = (t - 0.34) / 0.34;
    return [45 + k * 185, 172 + k * 30, 196 - k * 120];
  }

  const k = (t - 0.68) / 0.32;
  return [230 + k * 25, 202 - k * 95, 76 - k * 40];
}

export default function SpectrogramView({
  values,
  columns = 96,
  peakHz,
}: {
  values?: number[];
  columns?: number;
  peakHz?: number;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const context = canvas?.getContext('2d');
    if (!canvas || !context) return;
    const width = canvas.width;
    const height = canvas.height;
    const dataHeight = height - 12;
    const image = context.createImageData(width, dataHeight);
    const pixels = image.data;
    const count = Math.max(1, columns);

    for (let y = 0; y < dataHeight; y++) {
      const band = Math.max(0, Math.min(BANDS - 1, BANDS - 1 - Math.floor(y * BANDS / dataHeight)));
      for (let x = 0; x < width; x++) {
        const column = Math.max(0, Math.min(count - 1, Math.floor(x * count / width)));
        const value = values?.[column * BANDS + band] ?? 0;
        const [r, g, b] = color(value);
        const index = (y * width + x) * 4;
        pixels[index] = r;
        pixels[index + 1] = g;
        pixels[index + 2] = b;
        pixels[index + 3] = 255;
      }
    }

    context.putImageData(image, 0, 0);
    context.strokeStyle = 'rgba(255,255,255,0.08)';
    context.lineWidth = 1;
    for (const db of [100, 1000, 10000]) {
      const y = dataHeight - (Math.log(db / 20) / Math.log(1000)) * dataHeight;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(width, y);
      context.stroke();
    }

    context.fillStyle = 'rgba(255,255,255,0.4)';
    context.font = '9px Consolas, monospace';
    context.textBaseline = 'bottom';
    for (const [label, frequency] of [['100', 100], ['1k', 1000], ['10k', 10000]] as const) {
      const y = dataHeight - (Math.log(frequency / 20) / Math.log(1000)) * dataHeight;
      context.fillText(label, 2, Math.max(8, Math.min(dataHeight - 1, y - 1)));
    }

    context.fillStyle = 'rgba(255,255,255,0.42)';
    context.font = '9px Consolas, monospace';
    context.fillText(t('now'), width - 19, height - 1);
  }, [values, columns, t]);

  return (
    <div className="spectrogram-view">
      <canvas ref={ref} width={340} height={198} aria-label={t('Live spectrogram')} />
      <div className="spectrum-meta">
        <span>{t('4 s window')}</span>
        <strong>{formatHz(peakHz)}</strong>
      </div>
    </div>
  );
}
