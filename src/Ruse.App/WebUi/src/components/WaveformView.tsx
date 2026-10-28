import { useEffect, useRef, useState } from 'react';
import { useI18n } from '../i18n';

export default function WaveformView({
  samples,
  color,
  amplitude = 1,
  min = 0.1,
  max = 4,
  onAmplitudeChange,
}: {
  samples?: number[];
  color: string;
  amplitude?: number;
  min?: number;
  max?: number;
  onAmplitudeChange?: (value: number) => void;
}) {
  const { t } = useI18n();
  const ref = useRef<HTMLCanvasElement>(null);
  const [localAmplitude, setLocalAmplitude] = useState(amplitude);

  useEffect(() => setLocalAmplitude(amplitude), [amplitude]);

  useEffect(() => {
    const canvas = ref.current;
    const context = canvas?.getContext('2d');
    if (!canvas || !context) return;
    const { width, height } = canvas;
    context.clearRect(0, 0, width, height);
    context.strokeStyle = 'rgba(255,255,255,0.08)';
    context.lineWidth = 1;
    context.beginPath();
    context.moveTo(0, height / 2);
    context.lineTo(width, height / 2);
    context.stroke();
    if (!samples?.length) return;

    context.strokeStyle = color;
    context.lineWidth = 1.5;
    context.beginPath();
    samples.forEach((sample, index) => {
      const x = index / Math.max(1, samples.length - 1) * width;
      const y = height / 2 - Math.max(-1, Math.min(1, sample)) * (height / 2 - 3);
      if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
    });
    context.stroke();
  }, [samples, color]);

  return (
    <div className="waveform-view">
      <canvas ref={ref} width={184} height={52} aria-label={t('Live waveform')} />
      {onAmplitudeChange && (
        <label className="waveform-amplitude nodrag">
          <span>{t('Amplitude')}</span>
          <input
            type="range"
            min={min}
            max={max}
            step={0.01}
            value={localAmplitude}
            aria-label={t('Waveform amplitude')}
            onChange={event => {
              const next = Number(event.target.value);
              setLocalAmplitude(next);
              onAmplitudeChange(next);
            }}
          />
          <strong>{localAmplitude.toFixed(2)}×</strong>
        </label>
      )}
    </div>
  );
}
