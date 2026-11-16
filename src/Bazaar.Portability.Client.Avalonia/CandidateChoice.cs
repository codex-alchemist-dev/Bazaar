using System.Globalization;
using System.Windows.Input;
using Bazaar.Portability;

namespace Bazaar.Portability.Client.Avalonia;

/// <summary>One importer offered in the detect step, with the command that picks it.</summary>
public sealed class CandidateChoice(ImportCandidate candidate, ICommand chooseCommand)
{
    public string Id => candidate.Id;

    public string Reason => candidate.Reason;

    public string Confidence => candidate.Confidence.ToString("P0", CultureInfo.InvariantCulture);

    public ICommand ChooseCommand => chooseCommand;
}
