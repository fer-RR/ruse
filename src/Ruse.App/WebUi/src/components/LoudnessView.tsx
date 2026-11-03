import { send } from '../bridge';
import { useI18n } from '../i18n';

function format(value: number | undefined, digits = 1): string {
  return Number.isFinite(value) && value! > -119 ? value!.toFixed(digits) : '\u2014';
}

export default function LoudnessView({ id, integrated, shortTerm, lra, truePeak }: {
  id: string;
  integrated?: number;
  shortTerm?: number;
  lra?: number;
  truePeak?: number;
}) {
  const { t } = useI18n();
  return (
    <div className="loudness-view nodrag">
      <div className="loudness-main">
        <span>{t('Integrated')}</span>
        <strong>{format(integrated)}</strong>
        <em>LUFS</em>
      </div>
      <div className="loudness-metrics">
        <div>
          <span>{t('Short-term')}</span>
          <strong>{format(shortTerm)}</strong>
          <em>LUFS</em>
        </div>
        <div>
          <span>{t('Loudness range')}</span>
          <strong>{format(lra)}</strong>
          <em>LU</em>
        </div>
        <div>
          <span>{t('True peak')}</span>
          <strong>{format(truePeak)}</strong>
          <em>dBTP</em>
        </div>
      </div>
      <button className="small-button" onClick={() => send('loudness-reset', { id })}>{t('Reset loudness')}</button>
    </div>
  );
}
