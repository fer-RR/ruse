import { AlertTriangle, CheckCircle2, CircleHelp, Power, Volume2, VolumeX } from 'lucide-react';
import { send } from '../bridge';
import { useI18n } from '../i18n';
import { useGlobalTelemetry } from '../telemetry';
import type { GraphWarning, LinkModel } from '../types';
import type { RuseFlowNode } from '../flow';
import { Modal } from './Dialogs';

export default function RouteCheckDialog({ nodes, links, warnings, running, onOpenFirstRoute, onOpenMixer, onClose }: {
  nodes: RuseFlowNode[];
  links: LinkModel[];
  warnings?: GraphWarning[];
  running: boolean;
  onOpenFirstRoute: () => void;
  onOpenMixer: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const telemetry = useGlobalTelemetry();
  const audible = (id: string) => (telemetry.meters[id]?.level ?? 0) > 0.0008;
  const sources = nodes.filter(node => node.data.kind === 'source' && !node.data.diagnostics);
  const outputs = nodes.filter(node => node.data.kind === 'output' && !node.data.diagnostics);
  const modifiers = nodes.filter(node => node.data.kind === 'modifier' && !node.data.diagnostics);
  const muted = [...sources, ...modifiers, ...outputs].filter(node => node.data.muted);
  const sourceActive = sources.some(node => audible(node.data.id));
  const outputActive = outputs.some(node => audible(node.data.id));
  const noRoute = sources.length === 0 || outputs.length === 0 || links.length === 0;
  const warningsList = warnings ?? [];
  const healthy = running && !telemetry.masterMuted && !noRoute && muted.length === 0 && !sourceActive && !outputActive
    ? false
    : running && !telemetry.masterMuted && !noRoute && muted.length === 0 && sourceActive && outputActive && warningsList.length === 0;

  const unmute = (id: string) => send('mute', { id, value: false });

  return (
    <Modal title={t('Check route')} onClose={onClose} className="wide-modal route-check-modal">
      <div className="route-check-body">
        <div className={`route-check-summary ${healthy ? 'healthy' : ''}`}>
          <span className="route-check-summary-icon">{healthy ? <CheckCircle2 size={22} /> : <CircleHelp size={22} />}</span>
          <div>
            <strong>{healthy ? t('Good to go') : t('Check route')}</strong>
            <small>{healthy ? t('Signal detected') : t('Check the output node volume and Windows volume.')}</small>
          </div>
        </div>

        <div className="route-check-row">
          <span className={`route-check-dot ${running ? 'ok' : 'warn'}`} />
          <div><strong>{running ? t('Routing') : t('Routing is stopped')}</strong><small>{running ? t('Routing live') : t('Idle')}</small></div>
          {!running && <button type="button" className="tool-button" onClick={() => send('transport', { running: true })}><Power size={13} /> {t('Start routing')}</button>}
        </div>

        {noRoute && (
          <div className="route-check-row">
            <span className="route-check-dot warn" />
            <div><strong>{sources.length === 0 ? t('No sources on the canvas.') : outputs.length === 0 ? t('No outputs on the canvas.') : t('No cables yet')}</strong><small>{t('Add a source and an output, then drag from OUT to IN. Processing nodes sit between them.')}</small></div>
            <button type="button" className="tool-button" onClick={onOpenFirstRoute}>{t('Set up my first route')}</button>
          </div>
        )}

        {telemetry.masterMuted && (
          <div className="route-check-row">
            <span className="route-check-dot bad" />
            <div><strong>{t('Master output is muted')}</strong><small>{t('Mute all outputs')}</small></div>
            <button type="button" className="tool-button" onClick={() => send('master-mute', { value: false })}><VolumeX size={13} /> {t('Unmute all outputs')}</button>
          </div>
        )}

        {muted.length > 0 && (
          <div className="route-check-row">
            <span className="route-check-dot warn" />
            <div><strong>{t('Muted nodes')}</strong><small>{muted.map(node => node.data.title).join(', ')}</small></div>
            <div className="route-check-actions">
              {muted.slice(0, 4).map(node => <button type="button" key={node.id} className="tool-button" onClick={() => unmute(node.data.id)}><Volume2 size={13} /> {node.data.title}</button>)}
            </div>
          </div>
        )}

        {warningsList.length > 0 && (
          <div className="route-check-row">
            <span className="route-check-dot warn" />
            <div><strong>{t('A copy is also playing elsewhere')}</strong><small>{warningsList[0].message}</small></div>
          </div>
        )}

        {!noRoute && (
          <div className="route-check-signal">
            <div>
              <span className={`route-check-dot ${sourceActive ? 'ok' : 'warn'}`} />
              <strong>{t('Source')}</strong>
              <small>{sourceActive ? t('Signal detected') : t('No signal at the source')}</small>
            </div>
            <div>
              <span className={`route-check-dot ${outputActive ? 'ok' : 'warn'}`} />
              <strong>{t('Output')}</strong>
              <small>{outputActive ? t('Signal detected') : t('Waiting for signal')}</small>
            </div>
          </div>
        )}

        {!noRoute && sourceActive && !outputActive && (
          <div className="route-check-hint">
            <AlertTriangle size={15} />
            <span>{t('The signal stops between the source and the output.')}</span>
          </div>
        )}

        {!noRoute && !sourceActive && (
          <div className="route-check-hint">
            <AlertTriangle size={15} />
            <span>{t('Play something in the source or use the Test tone to check the route.')}</span>
          </div>
        )}

        <div className="route-check-map">
          <div><span className="eyebrow">{t('Sources')}</span>{sources.length === 0 ? <small>{t('None')}</small> : sources.map(node => <span key={node.id} className={audible(node.data.id) ? 'live' : ''}>{node.data.title}</span>)}</div>
          <div><span className="eyebrow">{t('Outputs')}</span>{outputs.length === 0 ? <small>{t('None')}</small> : outputs.map(node => <span key={node.id} className={audible(node.data.id) ? 'live' : ''}>{node.data.title}</span>)}</div>
        </div>
      </div>
      <div className="modal-actions">
        <button type="button" className="tool-button" onClick={onOpenMixer}>{t('Mixer')}</button>
        <button type="button" className="primary-button" onClick={onClose}>{t('Done')}</button>
      </div>
    </Modal>
  );
}
