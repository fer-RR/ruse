import { useEffect, useRef, useState } from 'react';
import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

export default function NoiseProfileControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  const [calibrating, setCalibrating] = useState(false);
  const [remainingMs, setRemainingMs] = useState(0);
  const timer = useRef<number | null>(null);

  const stopCalibration = () => {
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = null;
    setCalibrating(false);
    setRemainingMs(0);
  };

  useEffect(() => {
    if (node.noiseProfileName === 'Live calibration' || node.noiseProfileError) stopCalibration();
  }, [node.noiseProfileName, node.noiseProfileError]);

  useEffect(() => () => {
    if (timer.current !== null) window.clearInterval(timer.current);
  }, []);

  const calibrate = () => {
    send('noise-profile-calibrate', { id: node.id });
    setCalibrating(true);
    setRemainingMs(3000);
    if (timer.current !== null) window.clearInterval(timer.current);
    timer.current = window.setInterval(() => {
      setRemainingMs(current => {
        if (current <= 100) {
          if (timer.current !== null) window.clearInterval(timer.current);
          timer.current = null;
          setCalibrating(false);
          return 0;
        }

        return current - 100;
      });
    }, 100);
  };

  const cancelCalibration = () => {
    send('noise-profile-calibrate-cancel', { id: node.id });
    stopCalibration();
  };

  return (
    <div className="ir-controls nodrag">
      <div className="ir-name" title={node.noiseProfilePath ?? ''}>
        {node.noiseProfileName ? t(node.noiseProfileName) : t('No noise floor loaded')}
      </div>
      <div className="ir-actions">
        <button className="small-button" onClick={() => send('noise-profile-file', { id: node.id })}>{t('Load noise floor')}</button>
        {calibrating
          ? <button className="small-button" onClick={cancelCalibration}>{t('Cancel')} · {Math.ceil(remainingMs / 1000)}s</button>
          : <button className="small-button" title={t('Keep the room quiet for 3 seconds.')} onClick={calibrate}>{t('Calibrate from live noise')}</button>}
        {node.noiseProfilePath && <button className="small-button" onClick={() => send('noise-profile-clear', { id: node.id })}>{t('Clear')}</button>}
      </div>
      {node.noiseProfileError && <div className="node-error">{node.noiseProfileError}</div>}
    </div>
  );
}
