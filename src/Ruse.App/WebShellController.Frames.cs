using Ruse.App.Audio;

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

    private void SaveFrameTemplate(FrameRefPayload? request)
    {
        var frame = _frames.FirstOrDefault(item => item.Id == request?.Id);
        if (frame == null)
        {
            SetStatus("Select a group before saving a template.");
            return;
        }

        var included = new HashSet<string>(_nodes.Values
            .Where(entry =>
            {
                double width = entry.Width ?? 204;
                double height = entry.Height ?? 120;
                double centerX = entry.X + width / 2;
                double centerY = entry.Y + height / 2;
                return centerX >= frame.X && centerX <= frame.X + frame.Width &&
                       centerY >= frame.Y && centerY <= frame.Y + frame.Height;
            })
            .Select(entry => entry.Id));

        if (included.Count == 0)
        {
            SetStatus("The group is empty; add nodes before saving a template.");
            return;
        }

        var content = BuildPreset(included, -frame.X, -frame.Y);
        content.Name = frame.Name;
        content.Frames.Clear();
        FrameTemplateLibrary.Store(new FrameTemplate
        {
            Name = frame.Name,
            Width = frame.Width,
            Height = frame.Height,
            Content = content,
        });

        _catalog.FrameTemplates = BuildFrameTemplateCatalog();
        Post("catalog", _catalog);
        SetStatus($"Saved template '{frame.Name}' ({included.Count} nodes).");
    }

    private void InsertFrameTemplate(TemplateInsertPayload? request)
    {
        var template = FrameTemplateLibrary.Find(request?.Id);
        if (template == null)
        {
            SetStatus("That frame template no longer exists.");
            return;
        }

        double x = request?.X ?? 80;
        double y = request?.Y ?? 80;
        _frames.Add(new FrameDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = template.Name,
            X = x,
            Y = y,
            Width = Math.Clamp(template.Width, 120, 20000),
            Height = Math.Clamp(template.Height, 80, 20000),
        });

        ApplyPreset(template.Content, autoRun: false, insert: true, offsetX: x, offsetY: y,
            trustRecorderPaths: true);
        if (_running)
        {
            StartRouting();
        }
        else
        {
            PublishGraph();
        }

        SetStatus($"Inserted template '{template.Name}'.");
    }

    private void DeleteFrameTemplate(string name)
    {
        var template = FrameTemplateLibrary.Find(name);
        if (template == null)
        {
            return;
        }

        FrameTemplateLibrary.Remove(template);
        _catalog.FrameTemplates = BuildFrameTemplateCatalog();
        Post("catalog", _catalog);
        SetStatus($"Removed the template '{template.Name}'.");
    }

    private static List<CatalogItemDto> BuildFrameTemplateCatalog() =>
        FrameTemplateLibrary.Templates
            .Select(template => new CatalogItemDto
            {
                Id = template.Name,
                Kind = "template",
                Name = template.Name,
                Subtitle = $"{template.NodeCount} nodes",
                Details = "Frame template",
            })
            .ToList();
}
