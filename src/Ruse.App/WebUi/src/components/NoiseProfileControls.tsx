import { useEffect, useRef, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

export default function NoiseProfileControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [calibrating, setCalibrating] = useState(false);
  const timer = useRef<number | null>(null);

  useEffect(() => {
    if (node.noiseProfileName === 'Live calibration') setCalibrating(false);
  }, [node.noiseProfileName]);

  useEffect(() => () => {
    if (timer.current !== null) window.clearTimeout(timer.current);
  }, []);

  const calibrate = () => {
    send('noise-profile-calibrate', { id: node.id });
    setCalibrating(true);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setCalibrating(false), 6000);
  };

  return (
    <div className="ir-controls nodrag">
      <div className="ir-name" title={node.noiseProfilePath ?? ''}>
        {node.noiseProfileName ? t(node.noiseProfileName) : t('No noise floor loaded')}
      </div>
      <div className="ir-actions">
        <button className="small-button" onClick={() => send('noise-profile-file', { id: node.id })}>{t('Load noise floor')}</button>
        <button className="small-button" disabled={calibrating} title={t('Keep the room quiet for 3 seconds.')} onClick={calibrate}>{calibrating ? t('Calibrating...') : t('Calibrate from live noise')}</button>
        {node.noiseProfilePath && <button className="small-button" onClick={() => send('noise-profile-clear', { id: node.id })}>{t('Clear')}</button>}
      </div>
      {node.noiseProfileError && <div className="node-error">{node.noiseProfileError}</div>}
    </div>
  );
}
