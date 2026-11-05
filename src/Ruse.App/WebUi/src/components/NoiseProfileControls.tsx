import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

export default function NoiseProfileControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  return (
    <div className="ir-controls nodrag">
      <div className="ir-name" title={node.noiseProfilePath ?? ''}>
        {node.noiseProfileName || t('No noise floor loaded')}
      </div>
      <div className="ir-actions">
        <button className="small-button" onClick={() => send('noise-profile-file', { id: node.id })}>{t('Load noise floor')}</button>
        {node.noiseProfilePath && <button className="small-button" onClick={() => send('noise-profile-clear', { id: node.id })}>{t('Clear')}</button>}
      </div>
      {node.noiseProfileError && <div className="node-error">{node.noiseProfileError}</div>}
    </div>
  );
}
