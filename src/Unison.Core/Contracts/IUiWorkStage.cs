namespace Unison.Core.Contracts
{
    /// <summary>
    /// Labels the step a running UI dispatcher hop has reached, so a stall report
    /// can name the line inside the hop and not only the hop itself. UI thread only;
    /// outside a tracked hop the mark is dropped.
    /// </summary>
    public interface IUiWorkStage
    {
        void Mark(string stage);
    }
}
