using Avalonia.Controls;
using Bazaar.Client.Views;
using Bazaar.Rubedo.Views;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>
/// Builds the RyuseiFuda pane bodies from their Rubedo view documents (doc 41c). A head supplies the
/// <see cref="ViewContext"/> (theme, localiser, error callback, base actions) through the pane module context. With none,
/// the module keeps the code-built views, so a head that has not adopted view documents still works.
/// </summary>
internal static class RyuseiViewPanes
{
    private static readonly Lazy<ViewDocument> _schedule = new(() => ViewDocumentReader.Read(RyuseiScheduleViewData.ReadViewJson()));
    private static readonly Lazy<ViewDocument> _roadmap = new(() => ViewDocumentReader.Read(RyuseiRoadmapViewData.ReadViewJson()));
    private static readonly Lazy<ViewDocument> _workday = new(() => ViewDocumentReader.Read(RyuseiWorkdayViewData.ReadViewJson()));

    internal static Control Roadmap(RoadmapPaneViewModel pane, ViewContext head) =>
        new ViewMapper(head).Build(_roadmap.Value, RyuseiRoadmapViewData.From(pane));

    internal static Control Workday(WorkdayPaneViewModel pane, ViewContext head) =>
        new ViewMapper(head).Build(_workday.Value, RyuseiWorkdayViewData.From(pane));

    /// <summary>
    /// The schedule pane. Pause and Resume invoke <see cref="RyuseiScheduleViewData.PauseCommand"/> and
    /// <see cref="RyuseiScheduleViewData.ResumeCommand"/> with the row's id; the pane view model runs the change and the
    /// body is rebuilt from it, so the row shows a state only after the server confirmed it.
    /// </summary>
    internal static Control Schedule(RyuseiSchedulePaneViewModel pane, ViewContext head)
    {
        var host = new ContentControl();
        ViewMapper? mapper = null;

        void Render() => host.Content = mapper!.Build(_schedule.Value, RyuseiScheduleViewData.From(pane));

        mapper = new ViewMapper(head with { Actions = new ScheduleActions(pane, head.Actions, Render) });
        Render();
        return host;
    }

    /// <summary>Routes the two trigger commands to the pane view model and hands every other action to the head.</summary>
    private sealed class ScheduleActions(RyuseiSchedulePaneViewModel pane, IViewActions head, Action render) : IViewActions
    {
        public Task NavigateAsync(string target) => head.NavigateAsync(target);

        public Task InvokeAsync(string command) => head.InvokeAsync(command);

        public Task OpenPaneAsync(string paneKind) => head.OpenPaneAsync(paneKind);

        public Task SetSettingAsync(string key, string value) => head.SetSettingAsync(key, value);

        public Task NavigateAsync(string target, IReadOnlyDictionary<string, object> args) => head.NavigateAsync(target, args);

        public Task OpenPaneAsync(string paneKind, IReadOnlyDictionary<string, object> args) => head.OpenPaneAsync(paneKind, args);

        public async Task InvokeAsync(string command, IReadOnlyDictionary<string, object> args)
        {
            bool pause = command == RyuseiScheduleViewData.PauseCommand;
            if (!pause && command != RyuseiScheduleViewData.ResumeCommand)
            {
                await head.InvokeAsync(command, args).ConfigureAwait(true);
                return;
            }

            // The command declares its arguments: exactly one text id. Anything else fails the action closed.
            if (args.Count != 1 || !args.TryGetValue("id", out object? id) || id is not string triggerId)
            {
                throw new ArgumentException($"'{command}' takes one text argument, 'id'.");
            }

            await (pause ? pane.PauseAsync(triggerId) : pane.ResumeAsync(triggerId)).ConfigureAwait(true);
            render();
        }
    }
}
