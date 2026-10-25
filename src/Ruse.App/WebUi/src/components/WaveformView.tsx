import { useEffect, useRef } from 'react';

export default function WaveformView({ samples, color }: { samples?: number[]; color: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
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

  return <div className="waveform-view"><canvas ref={ref} width={184} height={52} aria-label="Live waveform" /></div>;
}
