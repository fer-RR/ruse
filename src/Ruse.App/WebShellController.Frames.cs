namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly List<FrameDto> _frames = new();

    private void SetFrames(FramesPayload? payload)
    {
        _frames.Clear();
        foreach (var frame in payload?.Frames ?? new List<FrameDto>())
        {
            if (string.IsNullOrWhiteSpace(frame.Id))
            {
                continue;
            }

            _frames.Add(new FrameDto
            {
                Id = frame.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(frame.Name) ? "Group" : frame.Name.Trim(),
                X = frame.X,
                Y = frame.Y,
                Width = Math.Clamp(frame.Width, 60, 20000),
                Height = Math.Clamp(frame.Height, 40, 20000),
            });
        }
    }
}
