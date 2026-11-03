import { send } from '../bridge';
import type { RuseFlowNode } from '../flow';
import { useI18n } from '../i18n';

export default function IrControls({ node }: { node: RuseFlowNode['data'] }) {
  const { t } = useI18n();
  return (
    <div className="ir-controls nodrag">
      <div className="ir-name" title={node.irPath ?? ''}>{node.irName ?? t('Default room')}</div>
      <div className="ir-actions">
        <button className="small-button" onClick={() => send('ir-file', { id: node.id })}>{t('Choose IR')}</button>
        {node.irPath && <button className="small-button" onClick={() => send('ir-clear', { id: node.id })}>{t('Use default')}</button>}
      </div>
      {node.irError && <div className="node-error">{node.irError}</div>}
    </div>
  );
}
