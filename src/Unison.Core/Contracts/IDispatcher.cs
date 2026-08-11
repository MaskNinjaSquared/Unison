using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IDispatcher
    {
        Task RunAsync(Action action);
    }
}
