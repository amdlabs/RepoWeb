using IVZVision.Core.Configuration;
using IVZVision.Core.Detection;
using IVZVision.Vision.Engine;
using IVZVision.Vision.Pipeline;
using IVZVision.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages;

public class DirectoModel : PageModel
{
    private readonly IConfigStore _config;
    private readonly CameraPipelineManager _pipeline;
    private readonly RecognitionEngine _engine;

    public DirectoModel(IConfigStore config, CameraPipelineManager pipeline, RecognitionEngine engine)
    {
        _config = config;
        _pipeline = pipeline;
        _engine = engine;
    }

    public IReadOnlyList<CameraConfig> Cameras { get; private set; } = Array.Empty<CameraConfig>();
    public IReadOnlyList<CameraStatus> Statuses { get; private set; } = Array.Empty<CameraStatus>();
    public IReadOnlyList<ObservationDto> Recent { get; private set; } = Array.Empty<ObservationDto>();
    public ModelStatus Models { get; private set; } = new();

    public void OnGet()
    {
        Cameras = _config.Current.Cameras.Where(c => c.Enabled).ToList();
        Statuses = _pipeline.Statuses;
        Models = _engine.Status;

        Recent = _pipeline.GetRecentObservations(take: 30)
            .Select(SignalRObservationSink.ToDto)
            .ToList();
    }

    public CameraStatus? StatusFor(Guid id) => Statuses.FirstOrDefault(s => s.CameraId == id);
}
