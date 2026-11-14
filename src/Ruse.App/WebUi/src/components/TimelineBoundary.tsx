import { Component, type ErrorInfo, type ReactNode } from 'react';
import { useI18n } from '../i18n';

interface TimelineBoundaryProps {
  children: ReactNode;
  onClose: () => void;
  onError: () => void;
}

interface TimelineBoundaryState {
  failed: boolean;
}

class Boundary extends Component<TimelineBoundaryProps, TimelineBoundaryState> {
  state: TimelineBoundaryState = { failed: false };

  static getDerivedStateFromError(): TimelineBoundaryState {
    return { failed: true };
  }

  componentDidCatch(_error: Error, _info: ErrorInfo): void {
    this.props.onError();
  }

  render(): ReactNode {
    if (this.state.failed) {
      return <TimelineError onClose={this.props.onClose} />;
    }

    return this.props.children;
  }
}

function TimelineError({ onClose }: { onClose: () => void }) {
  const { t } = useI18n();
  return (
    <div className="timeline-loading timeline-error">
      <span>{t('The timeline could not load.')}</span>
      <button type="button" className="tool-button" onClick={onClose}>{t('Close')}</button>
    </div>
  );
}

export default function TimelineBoundary(props: TimelineBoundaryProps) {
  return <Boundary {...props} />;
}
