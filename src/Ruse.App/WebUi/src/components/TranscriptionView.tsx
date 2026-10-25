export default function TranscriptionView({ status, transcript, partial }: {
  status?: string;
  transcript?: string;
  partial?: string;
}) {
  return (
    <div className="transcription-view">
      <div className="transcription-status">{status ?? 'Waiting for audio'}</div>
      <div className="transcription-text">
        {transcript || <span className="transcription-placeholder">Transcript will appear here.</span>}
        {partial && <span className="transcription-partial"> {partial}</span>}
      </div>
    </div>
  );
}
