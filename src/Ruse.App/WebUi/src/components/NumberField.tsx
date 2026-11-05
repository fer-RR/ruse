import { useEffect, useState } from 'react';
import { ChevronDown, ChevronUp } from 'lucide-react';
import { useI18n } from '../i18n';

export default function NumberField({ value, min, max, step = 1, onChange, disabled = false, ariaLabel }: {
  value: number;
  min?: number;
  max?: number;
  step?: number;
  onChange: (value: number) => void;
  disabled?: boolean;
  ariaLabel?: string;
}) {
  const { t } = useI18n();
  const [text, setText] = useState(String(value));

  useEffect(() => {
    setText(String(value));
  }, [value]);

  const clamp = (next: number) => Math.min(max ?? Infinity, Math.max(min ?? -Infinity, next));
  const commit = (next: number) => {
    const clamped = clamp(next);
    setText(String(clamped));
    onChange(clamped);
  };
  const commitText = () => {
    const next = Number(text);
    if (!Number.isFinite(next)) {
      setText(String(value));
      return;
    }

    commit(next);
  };
  const stepBy = (delta: number) => {
    const current = Number(text);
    commit((text.trim() !== '' && Number.isFinite(current) ? current : value) + delta);
  };

  return (
    <div className="number-field">
      <input
        type="number"
        min={min}
        max={max}
        step={step}
        value={text}
        disabled={disabled}
        aria-label={ariaLabel}
        onChange={event => setText(event.target.value)}
        onBlur={commitText}
        onKeyDown={event => {
          if (event.key === 'Enter') {
            event.preventDefault();
            commitText();
          }
        }}
      />
      <span className="number-field-arrows">
        <button type="button" tabIndex={-1} disabled={disabled} title={t('Increase')} aria-label={t('Increase')} onMouseDown={event => event.preventDefault()} onClick={() => stepBy(step)}>
          <ChevronUp size={10} />
        </button>
        <button type="button" tabIndex={-1} disabled={disabled} title={t('Decrease')} aria-label={t('Decrease')} onMouseDown={event => event.preventDefault()} onClick={() => stepBy(-step)}>
          <ChevronDown size={10} />
        </button>
      </span>
    </div>
  );
}
