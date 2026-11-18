import { useEffect, useMemo, useRef, useState } from 'react';
import type { ChangeEvent, PointerEvent as ReactPointerEvent } from 'react';
import { createPortal } from 'react-dom';
import { send } from '../bridge';
import type { ParameterModel } from '../types';
import { formatParameter } from './ParameterControl';
import { Modal } from './Dialogs';
import type { AudiogramData, HearingProfile } from '../audiogram';
import { HEARING_BANDS } from '../hearingBands';

const FREQUENCIES = HEARING_BANDS;

interface PendingImport {
  fileName: string;
  format: 'csv' | 'json';
  audiogram: AudiogramData;
  profile: HearingProfile;
}
const MIN_DB = -24;
const MAX_DB = 24;
const WIDTH = 340;
const HEIGHT = 104;
const TOP = 8;
const BOTTOM = HEIGHT - 18;

function formatFrequency(frequency: number): string {
  return frequency >= 1000
    ? `${Number((frequency / 1000).toFixed(1))} kHz`
    : `${frequency} Hz`;
}

function shortLabel(frequency: number): string {
  return frequency >= 1000
    ? `${Number((frequency / 1000).toFixed(1))}k`
    : `${frequency}`;
}

function xFor(frequency: number): number {
  return (Math.log(frequency / 20) / Math.log(20000 / 20)) * WIDTH;
}

function yFor(db: number): number {
  return BOTTOM - ((db - MIN_DB) / (MAX_DB - MIN_DB)) * (BOTTOM - TOP);
}

function dbFor(y: number): number {
  const ratio = 1 - (y - TOP) / Math.max(1, BOTTOM - TOP);
  return Math.round(Math.max(MIN_DB, Math.min(MAX_DB, MIN_DB + ratio * (MAX_DB - MIN_DB))) * 2) / 2;
}

function EarCurve({ ear, nodeId, parameters, accent }: {
  ear: 'Left' | 'Right';
  nodeId: string;
  parameters: ParameterModel[];
  accent: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const dragging = useRef(false);
  const [values, setValues] = useState<Record<number, number>>({});
  const bandParameters = useMemo(
    () => FREQUENCIES.map(frequency => parameters.find(parameter => parameter.name === `${ear} ${formatFrequency(frequency)}`)),
    [parameters, ear],
  );
  const gainParameter = parameters.find(parameter => parameter.name === `${ear} gain`);
  const [gain, setGain] = useState(gainParameter?.value ?? 0);

  useEffect(() => setGain(gainParameter?.value ?? 0), [gainParameter?.value]);

  useEffect(() => {
    if (dragging.current) return;
    const next: Record<number, number> = {};
    for (const parameter of bandParameters) {
      if (parameter) next[parameter.parameterIndex] = parameter.value;
    }
    setValues(next);
  }, [bandParameters]);

  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext('2d');
    if (!element || !context) return;

    context.clearRect(0, 0, WIDTH, HEIGHT);
    context.font = '8px Consolas, monospace';
    context.textBaseline = 'top';

    for (const db of [MAX_DB, 12, 0, -12, MIN_DB]) {
      const y = yFor(db);
      context.strokeStyle = db === 0 ? 'rgba(255,255,255,0.2)' : 'rgba(255,255,255,0.07)';
      context.lineWidth = 1;
      context.beginPath();
      context.moveTo(0, y);
      context.lineTo(WIDTH, y);
      context.stroke();
      context.fillStyle = 'rgba(255,255,255,0.28)';
      context.fillText(`${db > 0 ? '+' : ''}${db}`, 2, y - 9);
    }

    for (const frequency of FREQUENCIES) {
      const x = xFor(frequency);
      context.strokeStyle = 'rgba(255,255,255,0.045)';
      context.beginPath();
      context.moveTo(x, TOP);
      context.lineTo(x, BOTTOM);
      context.stroke();
      context.fillStyle = 'rgba(255,255,255,0.3)';
      const label = shortLabel(frequency);
      context.fillText(label, x - context.measureText(label).width / 2, BOTTOM + 4);
    }

    const points = FREQUENCIES.flatMap((frequency, index) => {
      const parameter = bandParameters[index];
      if (!parameter) return [];
      return [{ x: xFor(frequency), y: yFor(values[parameter.parameterIndex] ?? 0) }];
    });
    if (points.length === 0) return;

    context.beginPath();
    context.moveTo(0, yFor(0));
    context.lineTo(points[0].x, points[0].y);
    for (let index = 0; index < points.length - 1; index++) {
      const current = points[index];
      const next = points[index + 1];
      const midX = (current.x + next.x) / 2;
      const midY = (current.y + next.y) / 2;
      context.quadraticCurveTo(current.x, current.y, midX, midY);
    }
    context.lineTo(points[points.length - 1].x, points[points.length - 1].y);
    context.lineTo(WIDTH, yFor(0));
    context.closePath();
    context.fillStyle = accent;
    context.globalAlpha = 0.14;
    context.fill();
    context.globalAlpha = 1;

    context.beginPath();
    context.moveTo(points[0].x, points[0].y);
    for (let index = 0; index < points.length - 1; index++) {
      const current = points[index];
      const next = points[index + 1];
      const midX = (current.x + next.x) / 2;
      const midY = (current.y + next.y) / 2;
      context.quadraticCurveTo(current.x, current.y, midX, midY);
    }
    context.lineTo(points[points.length - 1].x, points[points.length - 1].y);
    context.strokeStyle = accent;
    context.lineWidth = 1.7;
    context.stroke();

    for (const point of points) {
      context.beginPath();
      context.arc(point.x, point.y, 3.2, 0, Math.PI * 2);
      context.fillStyle = '#11161e';
      context.fill();
      context.strokeStyle = accent;
      context.lineWidth = 1.4;
      context.stroke();
    }
  }, [values, bandParameters, accent]);

  const updateFromPointer = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const element = canvas.current;
    if (!element) return;
    const rect = element.getBoundingClientRect();
    const x = ((event.clientX - rect.left) / Math.max(1, rect.width)) * WIDTH;
    const y = ((event.clientY - rect.top) / Math.max(1, rect.height)) * HEIGHT;

    let nearest = 0;
    let distance = Infinity;
    FREQUENCIES.forEach((frequency, index) => {
      const delta = Math.abs(xFor(frequency) - x);
      if (delta < distance) {
        distance = delta;
        nearest = index;
      }
    });

    const parameter = bandParameters[nearest];
    if (!parameter) return;
    const value = dbFor(y);
    setValues(current => ({ ...current, [parameter.parameterIndex]: value }));
    send('param', {
      id: nodeId,
      processor: parameter.processorIndex,
      parameter: parameter.parameterIndex,
      value,
    });
  };

  const updateGain = (value: number) => {
    setGain(value);
    if (!gainParameter) return;
    send('param', {
      id: nodeId,
      processor: gainParameter.processorIndex,
      parameter: gainParameter.parameterIndex,
      value,
    });
  };

  return (
    <div className="hearing-ear">
      <div className="hearing-ear-head">
        <strong>{ear} ear</strong>
        <input
          type="range"
          min={-24}
          max={24}
          step={1}
          value={gain}
          aria-label={`${ear} ear gain`}
          onChange={event => updateGain(Number(event.target.value))}
        />
        <span>{gain > 0 ? '+' : ''}{Math.round(gain)} dB</span>
      </div>
      <canvas
        ref={canvas}
        width={WIDTH}
        height={HEIGHT}
        aria-label={`${ear} ear hearing profile`}
        onPointerDown={event => {
          dragging.current = true;
          event.currentTarget.setPointerCapture(event.pointerId);
          updateFromPointer(event);
        }}
        onPointerMove={event => {
          if (dragging.current) updateFromPointer(event);
        }}
        onPointerUp={event => {
          dragging.current = false;
          event.currentTarget.releasePointerCapture(event.pointerId);
        }}
        onPointerCancel={() => { dragging.current = false; }}
      />
    </div>
  );
}

function ProfileSlider({ label, parameter, onChange, disabled = false }: {
  label: string;
  parameter?: ParameterModel;
  onChange: (value: number) => void;
  disabled?: boolean;
}) {
  const [local, setLocal] = useState(parameter?.value ?? 0);
  useEffect(() => setLocal(parameter?.value ?? 0), [parameter?.value]);
  if (!parameter) return null;

  return (
    <label className="hearing-profile-row">
      <span>{label}</span>
      <input
        type="range"
        min={parameter.min}
        max={parameter.max}
        step={parameter.isInteger ? 1 : 0.5}
        value={local}
        disabled={disabled}
        onChange={event => {
          const value = Number(event.target.value);
          setLocal(value);
          onChange(value);
        }}
      />
      <b>{formatParameter(parameter, local)}</b>
    </label>
  );
}

function ProfileToggle({ label, parameter, onChange, disabled = false }: {
  label: string;
  parameter?: ParameterModel;
  onChange: (value: number) => void;
  disabled?: boolean;
}) {
  const [local, setLocal] = useState((parameter?.value ?? 0) >= 0.5);
  useEffect(() => setLocal((parameter?.value ?? 0) >= 0.5), [parameter?.value]);
  if (!parameter) return null;

  return (
    <label className="hearing-profile-toggle">
      <input
        type="checkbox"
        checked={local}
        disabled={disabled}
        onChange={event => {
          const value = event.target.checked ? 1 : 0;
          setLocal(event.target.checked);
          onChange(value);
        }}
      />
      <span>{label}</span>
    </label>
  );
}

function ImportEarSummary({ label, profile, apply, onChange }: {
  label: string;
  profile: HearingProfile['left'];
  apply: boolean;
  onChange: (value: boolean) => void;
}) {
  if (!profile) {
    return (
      <div className="hearing-import-ear missing">
        <strong>{label}</strong>
        <span>No thresholds found</span>
      </div>
    );
  }

  return (
    <label className="hearing-import-ear">
      <input type="checkbox" checked={apply} onChange={event => onChange(event.target.checked)} />
      <span>
        <strong>{label}</strong>
        <small>{profile.points.length} point{profile.points.length === 1 ? '' : 's'} · PTA4 {profile.pta?.toFixed(1) ?? '—'} dB HL · overall {profile.perEarGain > 0 ? '+' : ''}{profile.perEarGain} dB</small>
      </span>
      <b>{profile.severity}</b>
    </label>
  );
}

export default function HearingProfileView({ nodeId, parameters, accent }: {
  nodeId: string;
  parameters: ParameterModel[];
  accent: string;
}) {
  const fileInput = useRef<HTMLInputElement>(null);
  const [importError, setImportError] = useState('');
  const [pendingImport, setPendingImport] = useState<PendingImport | null>(null);
  const [applyLeft, setApplyLeft] = useState(true);
  const [applyRight, setApplyRight] = useState(true);

  const find = (name: string) => parameters.find(parameter => parameter.name === name);
  const sendValue = (name: string, value: number) => {
    const parameter = find(name);
    if (!parameter) return;
    send('param', {
      id: nodeId,
      processor: parameter.processorIndex,
      parameter: parameter.parameterIndex,
      value,
    });
  };

  const handleFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;

    setImportError('');
    try {
      const text = await file.text();
      const { parseAudiogram, buildHearingProfile } = await import('../audiogram');
      const audiogram = parseAudiogram(text, file.name);
      const profile = buildHearingProfile(audiogram);
      setPendingImport({ fileName: file.name, format: audiogram.format, audiogram, profile });
      setApplyLeft(Boolean(profile.left));
      setApplyRight(Boolean(profile.right));
    } catch (error) {
      setPendingImport(null);
      setImportError(error instanceof Error ? error.message : 'Could not read that audiogram file.');
    }
  };

  const applyImport = () => {
    if (!pendingImport) return;
    const { profile } = pendingImport;
    if (profile.left && applyLeft) {
      sendValue('Left gain', profile.left.perEarGain);
      for (let index = 0; index < FREQUENCIES.length; index++) {
        sendValue(`Left ${formatFrequency(FREQUENCIES[index])}`, profile.left.bandGains[index]);
      }
    }
    if (profile.right && applyRight) {
      sendValue('Right gain', profile.right.perEarGain);
      for (let index = 0; index < FREQUENCIES.length; index++) {
        sendValue(`Right ${formatFrequency(FREQUENCIES[index])}`, profile.right.bandGains[index]);
      }
    }
    setPendingImport(null);
  };

  const loudness = find('Loudness match');
  const loudnessEnabled = (loudness?.value ?? 0) >= 0.5;
  const limiter = find('Safety limiter');
  const limiterEnabled = (limiter?.value ?? 0) >= 0.5;

  const resetProfile = () => {
    for (const name of ['Left gain', 'Right gain']) sendValue(name, 0);
    for (const ear of ['Left', 'Right']) {
      for (const frequency of FREQUENCIES) sendValue(`${ear} ${formatFrequency(frequency)}`, 0);
    }
  };

  const copyEar = (from: 'Left' | 'Right', to: 'Left' | 'Right') => {
    for (const frequency of FREQUENCIES) {
      const source = find(`${from} ${formatFrequency(frequency)}`);
      if (source) sendValue(`${to} ${formatFrequency(frequency)}`, source.value);
    }
  };

  const canApply = Boolean(
    (pendingImport?.profile.left && applyLeft) || (pendingImport?.profile.right && applyRight),
  );

  return (
    <div className="hearing-profile-view nodrag">
      <EarCurve ear="Left" nodeId={nodeId} parameters={parameters} accent={accent} />
      <EarCurve ear="Right" nodeId={nodeId} parameters={parameters} accent={accent} />

      <div className="hearing-profile-actions">
        <input
          ref={fileInput}
          type="file"
          hidden
          accept=".csv,.json,text/csv,application/json"
          onChange={handleFile}
        />
        <button type="button" className="small-button" onClick={() => fileInput.current?.click()}>Import audiogram</button>
        <button type="button" className="small-button" onClick={resetProfile}>Reset profile</button>
        <button type="button" className="small-button" onClick={() => copyEar('Left', 'Right')}>Copy L -&gt; R</button>
        <button type="button" className="small-button" onClick={() => copyEar('Right', 'Left')}>Copy R -&gt; L</button>
      </div>

      {importError && <p className="hearing-profile-error">{importError}</p>}

      <div className="hearing-profile-controls">
        <ProfileSlider label="Monitor delay" parameter={find('Monitor delay')} onChange={value => sendValue('Monitor delay', value)} />
        <ProfileToggle label="Loudness match" parameter={loudness} onChange={value => sendValue('Loudness match', value)} />
        <ProfileSlider label="Target loudness" parameter={find('Target loudness')} onChange={value => sendValue('Target loudness', value)} disabled={!loudnessEnabled} />
        <ProfileSlider label="Max boost" parameter={find('Max boost')} onChange={value => sendValue('Max boost', value)} disabled={!loudnessEnabled} />
        <ProfileToggle label="Safety limiter" parameter={limiter} onChange={value => sendValue('Safety limiter', value)} />
        <ProfileSlider label="Limiter ceiling" parameter={find('Limiter ceiling')} onChange={value => sendValue('Limiter ceiling', value)} disabled={!limiterEnabled} />
      </div>

      <p className="hearing-profile-note">
        Place this last before the hearing output. The ear slider adds overall gain to the curve. Delays the mix to match video. Not a medical device; start at a low volume.
      </p>

      {pendingImport && createPortal(
        <Modal title="Import audiogram" onClose={() => setPendingImport(null)} className="hearing-import-modal">
          <div className="hearing-import-body">
            <div className="hearing-import-file">
              <strong>{pendingImport.fileName}</strong>
              <span>{pendingImport.format.toUpperCase()}</span>
            </div>
            {(pendingImport.audiogram.subject || pendingImport.audiogram.source || pendingImport.audiogram.notes) && (
              <p className="hearing-import-source">
                {[pendingImport.audiogram.subject, pendingImport.audiogram.source, pendingImport.audiogram.notes]
                  .filter(Boolean)
                  .join(' · ')}
              </p>
            )}

            <div className="hearing-import-ears">
              <ImportEarSummary label="Left ear" profile={pendingImport.profile.left} apply={applyLeft} onChange={setApplyLeft} />
              <ImportEarSummary label="Right ear" profile={pendingImport.profile.right} apply={applyRight} onChange={setApplyRight} />
            </div>

            {pendingImport.profile.warnings.length > 0 && (
              <ul className="hearing-import-warnings">
                {pendingImport.profile.warnings.map(warning => <li key={warning}>{warning}</li>)}
              </ul>
            )}

            <div className="hearing-import-table-wrap">
              <table className="hearing-import-table">
                <thead>
                  <tr><th>Hz</th><th>Left dB HL</th><th>Right dB HL</th><th>Left gain</th><th>Right gain</th></tr>
                </thead>
                <tbody>
                  {FREQUENCIES.map((frequency, index) => (
                    <tr key={frequency}>
                      <td>{shortLabel(frequency)}</td>
                      <td>{pendingImport.profile.left ? pendingImport.profile.left.thresholds[index].toFixed(1) : '—'}</td>
                      <td>{pendingImport.profile.right ? pendingImport.profile.right.thresholds[index].toFixed(1) : '—'}</td>
                      <td>{pendingImport.profile.left ? `${pendingImport.profile.left.appliedGains[index].toFixed(1)} dB` : '—'}</td>
                      <td>{pendingImport.profile.right ? `${pendingImport.profile.right.appliedGains[index].toFixed(1)} dB` : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <p className="hearing-import-note">
              dB HL thresholds are converted with a half-gain starting rule. Adjust by ear after import; this is not a clinical fitting.
            </p>
          </div>
          <div className="modal-actions">
            <button type="button" className="tool-button" onClick={() => setPendingImport(null)}>Cancel</button>
            <button type="button" className="primary-button" disabled={!canApply} onClick={applyImport}>Apply profile</button>
          </div>
        </Modal>,
        document.body,
      )}
    </div>
  );
}
