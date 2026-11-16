export interface TimelineStickyLabel {
  id: string;
  top: number;
  text: string;
  kind: 'track' | 'automation';
  muted?: boolean;
  solo?: boolean;
}

export default function TimelineLabelOverlay({ labels }: { labels: TimelineStickyLabel[] }) {
  return (
    <div className="timeline-sticky-labels" aria-hidden="true">
      {labels.map(label => (
        <div
          key={label.id}
          className={label.kind === 'track' ? `timeline-sticky-track-name ${label.muted ? 'muted' : ''}` : 'timeline-sticky-automation-name'}
          style={{ top: label.top }}
        >
          {label.kind === 'track'
            ? <span className="timeline-sticky-track-text">{label.text}</span>
            : label.text}
          {label.solo && <em>SOLO</em>}
        </div>
      ))}
    </div>
  );
}
